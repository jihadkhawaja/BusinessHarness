using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using BusinessHarness.Application;
using BusinessHarness.Core;
using BusinessHarness.Infrastructure;

namespace BusinessHarness.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly HttpClient client = SafeHttp.CreateClient();
    private readonly Dictionary<string, string> sessionSecrets = new(StringComparer.Ordinal);
    private string? directJiraToken;
    private readonly DesktopWorkspace workspace;
    private readonly DesktopSettingsStore settingsStore;
    private readonly List<ChatAttachment> attachments = [];
    private IReadOnlyList<DesktopConnectorInfo> catalogProviders = [];
    private IReadOnlyList<DesktopConnectorAction> catalogActions = [];
    private readonly Dictionary<string, TextBox> catalogInputs = new(StringComparer.Ordinal);
    private ChatSession? session;
    private CancellationTokenSource? operation;
    private bool plan;
    private Action? noticeAction;
    private Control? noticeReturnFocus;

    public MainWindow()
    {
        InitializeComponent();
        workspace = new(client, key => sessionSecrets.TryGetValue(key, out var val) ? val : Environment.GetEnvironmentVariable(key));
        var settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BusinessHarness", "Desktop");
        settingsStore = new(settingsDirectory, new DesktopOsKeyVault(settingsDirectory));
        Opened += async (_, _) =>
        {
            MainSurface.IsEnabled = false;
            SettingsPage.IsEnabled = false;
            try { await RestoreSettingsAsync(); }
            finally
            {
                MainSurface.IsEnabled = !NoticeOverlay.IsVisible && !ContextOverlay.IsVisible;
                SettingsPage.IsEnabled = !NoticeOverlay.IsVisible;
            }
        };

        var envModel = Environment.GetEnvironmentVariable("HARNESS_MODEL");
        var envProvider = Environment.GetEnvironmentVariable("HARNESS_PROVIDER");
        ProviderBox.SelectedIndex = envProvider == "anthropic" ? 1 : 0;
        ModelBox.Text = !string.IsNullOrWhiteSpace(envModel) ? envModel : (ProviderBox.SelectedIndex == 1 ? "claude-3-7-sonnet-20250219" : "gpt-4o");

        UpdateJiraUi();
        catalogProviders = DesktopConnectorCatalog.Providers();
        CatalogProviderBox.ItemsSource = catalogProviders.Select(p => p.Name).ToArray();
        SizeChanged += (_, _) => { if (Width < 900) Sidebar.IsVisible = false; };
        Closed += (_, _) => { operation?.Cancel(); client.Dispose(); };
    }

    private void Overlay_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && e.Source == border)
        {
            CloseOverlays();
        }
    }

    private void ToggleSidebar_Click(object? sender, RoutedEventArgs e) => Sidebar.IsVisible = !Sidebar.IsVisible;

    private void OpenConnections_Click(object? sender, RoutedEventArgs e)
    {
        SettingsStatus.IsVisible = false;
        ContextOverlay.IsVisible = false;
        SettingsPage.IsVisible = true;
        MainSurface.IsVisible = false;
        SettingsBackButton.Focus();
    }

    private void CloseConnections_Click(object? sender, RoutedEventArgs e) => CloseOverlays();

    private void OpenContext_Click(object? sender, RoutedEventArgs e)
    {
        ContextStatus.IsVisible = false;
        SettingsPage.IsVisible = false;
        ContextOverlay.IsVisible = true;
        MainSurface.IsEnabled = false;
        ReferenceBox.Focus();
    }

    private void CloseContext_Click(object? sender, RoutedEventArgs e) => CloseOverlays();

    private void CloseOverlays()
    {
        SettingsPage.IsVisible = false;
        ContextOverlay.IsVisible = false;
        MainSurface.IsVisible = true;
        MainSurface.IsEnabled = true;
        PromptBox.Focus();
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && NoticeOverlay.IsVisible)
        {
            DismissNotice();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && (SettingsPage.IsVisible || ContextOverlay.IsVisible))
        {
            CloseOverlays();
            e.Handled = true;
        }
    }

    private void AccessMode_Changed(object? sender, RoutedEventArgs e)
    {
        if (AccountOptions is null || TokenOptions is null || AccountMode is null) return;
        var account = AccountMode.IsChecked == true;
        AccountOptions.IsVisible = account;
        TokenOptions.IsVisible = !account;
    }

    private void ProviderBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ModelBox is null) return;
        if (CredentialBox is not null) CredentialBox.Text = "";
        if (string.IsNullOrWhiteSpace(ModelBox.Text) || ModelBox.Text is "gpt-4o" or "claude-3-7-sonnet-20250219")
        {
            ModelBox.Text = ProviderBox.SelectedIndex == 1 ? "claude-3-7-sonnet-20250219" : "gpt-4o";
        }
    }

    private void ReferenceKindBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ReferenceBox is null) return;
        ReferenceBox.PlaceholderText = ReferenceKindBox.SelectedIndex switch
        {
            0 => "Jira ticket key (e.g. PROJ-123)",
            1 => "Confluence numeric page ID (e.g. 12345)",
            _ => "Identifier (e.g. PROJ-123 or SOURCE:ID)"
        };
    }

    private void ToggleAppPicker_Click(object? sender, RoutedEventArgs e) => AppPicker.IsVisible = !AppPicker.IsVisible;

    private void SelectApp(Control selected)
    {
        AppPicker.IsVisible = false;
        foreach (var detail in new Control[] { JiraDetails, GitHubDetails, GitLabDetails, CatalogDetails, PolicyDetails })
            detail.IsVisible = ReferenceEquals(detail, selected);
    }

    private void SelectJira_Click(object? sender, RoutedEventArgs e) => SelectApp(JiraDetails);
    private void SelectGitHub_Click(object? sender, RoutedEventArgs e) => SelectApp(GitHubDetails);
    private void SelectGitLab_Click(object? sender, RoutedEventArgs e) => SelectApp(GitLabDetails);
    private void SelectPolicy_Click(object? sender, RoutedEventArgs e) => SelectApp(PolicyDetails);
    private void SelectCatalog_Click(object? sender, RoutedEventArgs e) => SelectApp(CatalogDetails);

    private DesktopConnectorInfo? SelectedCatalogProvider() => CatalogProviderBox.SelectedIndex >= 0 && CatalogProviderBox.SelectedIndex < catalogProviders.Count
        ? catalogProviders[CatalogProviderBox.SelectedIndex] : null;

    private void CatalogProvider_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (CatalogConnectionFields is null || CatalogProviderBox is null || CatalogStatus is null || workspace is null) return;
        var selected = SelectedCatalogProvider();
        CatalogCredentialBox.Text = "";
        CatalogBaseBox.Text = "";
        CatalogParametersPanel.Children.Clear();
        catalogInputs.Clear();
        if (selected is null) { CatalogConnectionFields.IsVisible = false; CatalogReadFields.IsVisible = false; return; }
        CatalogConnectionFields.IsVisible = selected.HasReadActions;
        CatalogBaseBox.IsVisible = selected.RequiresBase;
        CatalogBaseBox.PlaceholderText = selected.Id == "shopify" ? "https://your-store.myshopify.com/" : "Your app's HTTPS API base URL";
        CatalogCredentialHint.Text = selected.CredentialDescription;
        CatalogCredentialBox.IsVisible = selected.RequiresCredential;
        var connected = workspace.ConnectorSettings.TryGetValue(selected.Id, out var existing);
        CatalogStatus.Text = !selected.HasReadActions ? "No supported read actions for this app yet." : connected
            ? selected.RequiresBase ? $"Active API: {existing!.BaseUri}" : $"Active app: {selected.Name}"
            : "Not connected";
        if (connected) CatalogBaseBox.Text = existing!.BaseUri;
        catalogActions = DesktopConnectorCatalog.Actions(selected.Id);
        CatalogActionBox.SelectedIndex = -1;
        CatalogActionBox.ItemsSource = catalogActions.Select(a => a.Name).ToArray();
        CatalogActionBox.SelectedIndex = catalogActions.Count == 1 ? 0 : -1;
        CatalogReadFields.IsVisible = selected.HasReadActions && workspace.HasCatalogApp(selected.Id);
    }

    private void CatalogAction_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (CatalogParametersPanel is null || CatalogActionBox is null) return;
        CatalogParametersPanel.Children.Clear();
        catalogInputs.Clear();
        if (CatalogActionBox.SelectedIndex < 0 || CatalogActionBox.SelectedIndex >= catalogActions.Count) return;
        foreach (var parameter in catalogActions[CatalogActionBox.SelectedIndex].Parameters)
        {
            var label = parameter.Name.Replace('_', ' ');
            var suffix = parameter.Example is not null ? $" (e.g. {parameter.Example})" : parameter.Required ? " (required)" : " (optional)";
            var input = new TextBox { PlaceholderText = label + suffix, MaxLength = 2000 };
            ToolTip.SetTip(input, parameter.Kind);
            Avalonia.Automation.AutomationProperties.SetName(input, label);
            catalogInputs.Add(parameter.Name, input);
            CatalogParametersPanel.Children.Add(input);
        }
    }

    private void CatalogConnect_Click(object? sender, RoutedEventArgs e)
    {
        if (operation is not null || SelectedCatalogProvider() is not { } selected) return;
        try
        {
            workspace.ConnectCatalogApp(selected.Id, new(CatalogBaseBox.Text?.Trim() ?? "", CatalogCredentialBox.Text?.Trim() ?? ""));
            CatalogCredentialBox.Text = "";
            CatalogStatus.Text = selected.RequiresBase ? $"Active API: {CatalogBaseBox.Text?.Trim()}" : $"Active app: {selected.Name}";
            CatalogReadFields.IsVisible = true;
        }
        catch (HarnessException ex) { ShowFailure(ex); }
    }

    private async void CatalogAdd_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedCatalogProvider() is not { } selected || CatalogActionBox.SelectedIndex < 0 || CatalogActionBox.SelectedIndex >= catalogActions.Count) return;
        workspace.ConnectorSettings.TryGetValue(selected.Id, out var applied);
        try { DesktopSettingsDraft.EnsureCatalogApplied(applied, selected.RequiresBase, CatalogBaseBox.Text, CatalogCredentialBox.Text); }
        catch (HarnessException ex) { ShowFailure(ex); return; }
        var action = catalogActions[CatalogActionBox.SelectedIndex];
        var values = catalogInputs.Where(p => !string.IsNullOrWhiteSpace(p.Value.Text))
            .ToDictionary(p => p.Key, p => p.Value.Text!.Trim(), StringComparer.Ordinal);
        await RunAsync(async ct =>
        {
            AddAttachments([await workspace.ReadCatalogAppAsync(selected.Id, action.Id, values, ct)]);
            CloseOverlays();
            SetStatus("Added to chat.");
        });
    }

    private void UpdateRedaction() => workspace.UpdateConnectionSecrets(sessionSecrets.Values);

    private void SaveGitHub_Click(object? sender, RoutedEventArgs e) => SaveHostToken("GITHUB_TOKEN", HostTokenBox.Text, GitHubStatus);
    private void SaveGitLab_Click(object? sender, RoutedEventArgs e) => SaveHostToken("GITLAB_TOKEN", GitLabTokenBox.Text, GitLabStatus);

    private void SaveHostToken(string name, string? input, TextBlock status)
    {
        var value = input?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            ShowNotice("Token needed", "Enter a valid read-only token to continue.");
            return;
        }
        try { workspace.UpdateConnectionSecrets([value]); }
        catch (HarnessException ex) { ShowFailure(ex); return; }
        sessionSecrets[name] = value;
        status.Text = "Ready for this session. Save settings to keep this token.";
    }

    private void ConnectJira_Click(object? sender, RoutedEventArgs e)
    {
        if (operation is not null) return;
        try
        {
            var token = JiraTokenBox.Text?.Trim() ?? "";
            workspace.ConnectAtlassian(JiraSiteBox.Text?.Trim() ?? "", JiraEmailBox.Text?.Trim() ?? "", token);
            directJiraToken = token;
            JiraTokenBox.Text = "";
            UpdateJiraUi();
        }
        catch (HarnessException ex) { ShowFailure(ex); }
    }

    private void DisconnectJira_Click(object? sender, RoutedEventArgs e)
    {
        if (operation is not null) return;
        workspace.DisconnectAtlassian();
        directJiraToken = null;
        JiraSiteBox.Text = "";
        JiraEmailBox.Text = "";
        JiraTokenBox.Text = "";
        SettingsStatus.Text = "Disconnected. Save settings to update the saved copy.";
        SettingsStatus.IsVisible = true;
        UpdateJiraUi();
    }

    private void UpdateJiraUi()
    {
        JiraConnectButton.IsVisible = !workspace.HasAtlassian;
        JiraDisconnectButton.IsVisible = workspace.HasAtlassian;
        JiraInputs.IsVisible = !workspace.HasAtlassian;
        JiraStatus.Text = workspace.HasAtlassian ? $"Ready: {workspace.AtlassianSite}. Access is checked when you add an item." : "Not connected";
    }

    private async void SaveSettings_Click(object? sender, RoutedEventArgs e)
    {
        await RunAsync(async ct =>
        {
            DesktopSettingsDraft.EnsureConnectionsApplied(sessionSecrets, HostTokenBox.Text, GitLabTokenBox.Text,
                workspace.HasAtlassian, JiraSiteBox.Text, JiraEmailBox.Text, JiraTokenBox.Text);
            var selectedApp = SelectedCatalogProvider();
            DesktopConnectorSettings? appliedApp = null;
            if (selectedApp is not null) workspace.ConnectorSettings.TryGetValue(selectedApp.Id, out appliedApp);
            DesktopSettingsDraft.EnsureCatalogApplied(appliedApp, selectedApp?.RequiresBase == true, CatalogBaseBox.Text, CatalogCredentialBox.Text);
            var values = new Dictionary<string, string>(sessionSecrets, StringComparer.Ordinal);
            if (workspace.HasAtlassian && directJiraToken is { } jiraToken) values["DIRECT_JIRA_API_TOKEN"] = jiraToken;
            var activeCredential = CredentialBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(activeCredential))
            {
                var name = ProviderBox.SelectedIndex == 2 ? "OPENAI_CHATGPT_ACCESS_TOKEN" : ProviderBox.SelectedIndex == 1 ? "ANTHROPIC_API_KEY" : "OPENAI_API_KEY";
                values[name] = activeCredential;
            }
            workspace.UpdateConnectionSecrets(values.Values);
            var settings = new DesktopSettings(ProviderBox.SelectedIndex, ModelBox.Text?.Trim() ?? "", PolicyBox.Text?.Trim() ?? "",
                workspace.HasAtlassian ? workspace.AtlassianSite! : "", workspace.HasAtlassian ? JiraEmailBox.Text?.Trim() ?? "" : "", values,
                new(workspace.ConnectorSettings, StringComparer.Ordinal));
            await Task.Run(() => settingsStore.SaveAsync(settings, ct), ct);
            SetStatus("Settings saved securely for this OS user.");
        });
    }

    private void ForgetSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (operation is not null) return;
        ShowNotice("Forget saved settings?", "The saved copy will be removed. Your current session stays open.", "Forget", async () =>
            await RunAsync(async ct =>
            {
                await Task.Run(() => settingsStore.ClearAsync(ct), ct);
                SetStatus("Saved settings removed. Current connections stay open until you exit.");
            }));
    }

    private async Task RestoreSettingsAsync()
    {
        try
        {
            var saved = await Task.Run(() => settingsStore.LoadAsync(CancellationToken.None));
            if (saved is null) return;
            ProviderBox.SelectedIndex = saved.Provider;
            ModelBox.Text = saved.Model;
            PolicyBox.Text = saved.PolicyPath;
            foreach (var entry in DesktopSettingsDraft.RuntimeSecrets(saved.Secrets)) sessionSecrets[entry.Key] = entry.Value;
            UpdateRedaction();
            foreach (var entry in saved.Connectors ?? []) workspace.ConnectCatalogApp(entry.Key, entry.Value);
            if (saved.JiraSite.Length > 0)
            {
                directJiraToken = DesktopSettingsDraft.DirectJiraToken(saved.Secrets)!;
                workspace.ConnectAtlassian(saved.JiraSite, saved.JiraEmail, directJiraToken);
                JiraSiteBox.Text = saved.JiraSite;
                JiraEmailBox.Text = saved.JiraEmail;
                UpdateJiraUi();
            }
            var provider = saved.Provider == 1 ? "anthropic" : "openai";
            var isPlan = saved.Provider == 2;
            var credentialName = isPlan ? "OPENAI_CHATGPT_ACCESS_TOKEN" : provider == "openai" ? "OPENAI_API_KEY" : "ANTHROPIC_API_KEY";
            if (sessionSecrets.ContainsKey(credentialName))
            {
                await workspace.ConfigureAsync(saved.PolicyPath, provider, saved.Model, CancellationToken.None);
                UpdateRedaction();
                plan = isPlan;
                session = workspace.CreateChat(plan);
                ModelButton.Content = $"{(plan ? "ChatGPT plan" : provider + " · " + workspace.Policy.Model)}  ⌄";
                SidebarStatus.Text = $"Ready ({workspace.Policy.Model})";
                AiStatus.Text = $"Connected to {workspace.Policy.Model}";
            }
            if (sessionSecrets.ContainsKey("GITHUB_TOKEN")) GitHubStatus.Text = "Saved for this OS user.";
            if (sessionSecrets.ContainsKey("GITLAB_TOKEN")) GitLabStatus.Text = "Saved for this OS user.";
        }
        catch (Exception)
        {
            ShowNotice("Couldn’t restore settings", "Check your OS credential store and saved settings. You can continue with session-only connections.");
        }
    }

    private async void ChoosePolicy_Click(object? sender, RoutedEventArgs e)
    {
        await RunAsync(async ct =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new()
            {
                Title = "Select a trusted BusinessHarness policy",
                AllowMultiple = false,
                FileTypeFilter = [new("JSON policy") { Patterns = ["*.json"] }]
            });
            ct.ThrowIfCancellationRequested();
            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath();
            if (path is null) throw new HarnessException(ErrorCode.Configuration, "Select a local policy file.", "Use trusted JSON from outside the reviewed branch.");
            PolicyBox.Text = path;
            SetStatus("Policy selected. Click Connect to apply.");
        });
    }

    private async void ImportCredentials_Click(object? sender, RoutedEventArgs e)
    {
        await RunAsync(async ct =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new()
            {
                Title = "Select a credentials file",
                AllowMultiple = false,
                FileTypeFilter = [new("Credentials") { Patterns = [".credentials", "*.credentials", "*.json"] }]
            });
            ct.ThrowIfCancellationRequested();
            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath();
            if (path is null) throw new HarnessException(ErrorCode.Configuration, "Select a local credentials file.", "Use a supported JSON credentials file.");
            var (provider, model, key) = await DesktopCredentialImport.ReadAsync(path, ct);
            TokenMode.IsChecked = true;
            ProviderBox.SelectedIndex = provider == "anthropic" ? 1 : 0;
            ModelBox.Text = model;
            CredentialBox.Text = key;
            SettingsStatus.Text = $"Imported {provider} credentials for {model}. Click Connect to apply.";
            SettingsStatus.IsVisible = true;
        });
    }

    private async void Connect_Click(object? sender, RoutedEventArgs e)
    {
        await RunAsync(async ct =>
        {
            session = null;
            ResetConversation();
            plan = ProviderBox.SelectedIndex == 2;
            var provider = ProviderBox.SelectedIndex switch
            {
                1 => "anthropic",
                _ => "openai"
            };
            var model = ModelBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(model))
            {
                model = provider == "anthropic" ? "claude-3-7-sonnet-20250219" : "gpt-4o";
                ModelBox.Text = model;
            }

            var cred = CredentialBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(cred))
            {
                var secretName = plan ? "OPENAI_CHATGPT_ACCESS_TOKEN" : provider == "openai" ? "OPENAI_API_KEY" : "ANTHROPIC_API_KEY";
                sessionSecrets[secretName] = cred;
            }

            await workspace.ConfigureAsync(PolicyBox.Text, provider, model, ct);
            UpdateRedaction();
            session = workspace.CreateChat(plan);

            var modelDisplay = plan ? "ChatGPT plan" : $"{provider} · {workspace.Policy.Model}";
            ModelButton.Content = $"{modelDisplay}  ⌄";
            SidebarStatus.Text = $"Ready ({workspace.Policy.Model})";
            AiStatus.Text = $"Connected to {modelDisplay}";
            CloseOverlays();
            SetStatus("");
        });
    }

    private void NewChat_Click(object? sender, RoutedEventArgs e)
    {
        if (operation is not null) return;
        if (ConversationPanel.Children.Count > 0 || attachments.Count > 0 || !string.IsNullOrWhiteSpace(PromptBox.Text))
        {
            ShowNotice("Start a new chat?", "Your current conversation and attachments will be cleared.", "New chat", StartNewChat);
            return;
        }
        StartNewChat();
    }

    private void StartNewChat()
    {
        try
        {
            if (session is not null) session = workspace.CreateChat(plan);
            ResetConversation();
            SetStatus("");
        }
        catch (HarnessException ex) { ShowFailure(ex); }
    }

    private void ResetConversation()
    {
        attachments.Clear();
        RefreshAttachments();
        ConversationPanel.Children.Clear();
        WelcomePanel.IsVisible = true;
        ConversationScroll.IsVisible = false;
        PromptBox.Text = "";
        SetStatus("");
    }

    private async void LoadPr_Click(object? sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        RequireConnection();
        var url = PrUrlBox.Text?.Trim();
        var list = await workspace.ReadChangeRequestAsync(string.IsNullOrWhiteSpace(url) ? null : url, ct);
        AddAttachments(list);
        CloseOverlays();
        SetStatus("PR metadata and references attached.");
    });

    private async void LoadReferences_Click(object? sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        var raw = ReferenceBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(raw))
            throw new HarnessException(ErrorCode.Configuration, "Enter an identifier.", "Use a Jira key (e.g. PROJ-123) or Confluence page ID.");

        var kindIndex = ReferenceKindBox.SelectedIndex;
        if (kindIndex is 0 or 1)
        {
            var kind = kindIndex == 0 ? "jira" : "confluence";
            AddAttachments([await workspace.ReadAtlassianAsync(kind, raw, ct)]);
            CloseOverlays();
            SetStatus("Added to chat.");
        }
        else
        {
            RequireConnection();
            AddAttachments(await workspace.ReadReferencesAsync(raw, ct));
            CloseOverlays();
            SetStatus("References attached to chat.");
        }
    });

    private async void ChooseRepository_Click(object? sender, RoutedEventArgs e)
    {
        await RunAsync(async ct =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new()
            {
                Title = "Select Git repository folder",
                AllowMultiple = false
            });
            ct.ThrowIfCancellationRequested();
            if (folders.Count == 0) return;
            var path = folders[0].TryGetLocalPath();
            if (path is null) throw new HarnessException(ErrorCode.Configuration, "Select a local repository folder.", "Use a local Git repository on disk.");
            RepositoryBox.Text = path;
        });
    }

    private async void LoadDiff_Click(object? sender, RoutedEventArgs e) => await RunAsync(async ct =>
    {
        RequireConnection();
        var repo = RepositoryBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(repo))
            throw new HarnessException(ErrorCode.Configuration, "Repository folder is required.", "Select the root folder of your local git repository.");
        var baseRef = string.IsNullOrWhiteSpace(BaseBox.Text) ? "origin/main" : BaseBox.Text.Trim();
        var headRef = string.IsNullOrWhiteSpace(HeadBox.Text) ? "HEAD" : HeadBox.Text.Trim();
        var diff = await workspace.ReadDiffAsync(repo, baseRef, headRef, ct);
        AddAttachments([diff]);
        CloseOverlays();
        SetStatus("Git diff attached.");
    });

    private void AddAttachments(IReadOnlyList<ChatAttachment> incoming)
    {
        var updated = attachments.Where(a => !incoming.Any(n => n.Name == a.Name)).Concat(incoming).ToArray();
        ChatSession.Validate(new([new("user", "Context preview")], updated));
        attachments.Clear();
        attachments.AddRange(updated);
        RefreshAttachments();
    }

    private void RefreshAttachments()
    {
        AttachmentChips.Children.Clear();
        if (attachments.Count == 0)
        {
            AttachmentChips.IsVisible = false;
            return;
        }
        AttachmentChips.IsVisible = true;
        foreach (var attachment in attachments)
        {
            var chip = new Border
            {
                Background = Brush.Parse("#383838"),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(10, 4, 8, 4),
                Margin = new Thickness(0, 0, 6, 6)
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            sp.Children.Add(new TextBlock
            {
                Text = "📎 " + Truncate(attachment.Name, 32),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush.Parse("#E0E0E0")
            });
            var removeBtn = new Button
            {
                Content = "×",
                Classes = { "quiet" },
                Padding = new Thickness(4, 0),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush.Parse("#A0A0A0")
            };
            ToolTip.SetTip(removeBtn, "Remove attachment");
            var current = attachment;
            removeBtn.Click += (_, _) =>
            {
                attachments.Remove(current);
                RefreshAttachments();
            };
            sp.Children.Add(removeBtn);
            chip.Child = sp;
            AttachmentChips.Children.Add(chip);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 3)] + "…";

    private async void Send_Click(object? sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendAsync()
    {
        if (operation is not null || string.IsNullOrWhiteSpace(PromptBox.Text)) return;
        await RunAsync(async ct =>
        {
            RequireConnection();
            var text = PromptBox.Text!.Trim();
            SetStatus("Thinking…");
            var reply = await session!.SendAsync(text, attachments, ct);
            PromptBox.Text = "";
            WelcomePanel.IsVisible = false;
            ConversationScroll.IsVisible = true;
            AddMessage("YOU", text);
            AddMessage("BUSINESSHARNESS", reply);
            ConversationScroll.ScrollToEnd();
            SetStatus("");
        });
    }

    private void AddMessage(string author, string text)
    {
        var isUser = author == "YOU";
        var content = new StackPanel { Spacing = 6 };
        if (!isUser)
        {
            content.Children.Add(new TextBlock
            {
                Text = "✦ BusinessHarness",
                FontSize = 12,
                Foreground = Brush.Parse("#10A37F"),
                FontWeight = FontWeight.SemiBold
            });
        }
        content.Children.Add(new SelectableTextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            Foreground = Brush.Parse("#ECECEC")
        });

        var bubble = new Border
        {
            Background = Brush.Parse(isUser ? "#2F2F2F" : "Transparent"),
            CornerRadius = new CornerRadius(isUser ? 18 : 8),
            Padding = new Thickness(isUser ? 14 : 0, isUser ? 10 : 8, isUser ? 14 : 0, isUser ? 10 : 8),
            MaxWidth = isUser ? 620 : 760,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = content
        };

        ConversationPanel.Children.Add(bubble);
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, TimeSpan? timeout = null)
    {
        if (operation is not null) return;
        using var current = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(workspace.Policy.TimeoutSeconds));
        operation = current;
        SetBusy(true);
        try { await action(current.Token); }
        catch (HarnessException ex) { ShowFailure(ex); }
        catch (OperationCanceledException) { ShowNotice("Stopped", "The operation was cancelled or timed out. Your draft is still here."); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ShowNotice("Couldn’t open this", "Check the selected file or folder and try again.");
        }
        catch (Exception) { ShowNotice("Something went wrong", "Please try again. Your draft is still here."); }
        finally { operation = null; SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        BusyIndicator.IsVisible = busy;
        CancelButton.IsVisible = busy;
        SettingsCancelButton.IsVisible = busy;
        ContextCancelButton.IsVisible = busy;
        CancelButton.IsEnabled = busy;
        SendButton.IsVisible = !busy;
        foreach (var control in new Control[] { AttachButton, AccountMode, TokenMode, ConnectButton, SaveSettingsButton, ForgetSettingsButton, AddAppButton, GitHubSaveButton, GitLabSaveButton, JiraConnectButton, JiraDisconnectButton, CredentialBox, HostTokenBox, GitLabTokenBox, JiraSiteBox, JiraEmailBox, JiraTokenBox, CatalogProviderBox, CatalogActionBox, CatalogBaseBox, CatalogCredentialBox, CatalogConnectButton, CatalogAddButton, CatalogParametersPanel, PrUrlBox, PrButton, ReferencesButton, DiffButton,
                     ProviderBox, ModelBox, PolicyBox, ReferenceBox, RepositoryBox, BaseBox, HeadBox, PromptBox })
            control.IsEnabled = !busy;
    }

    private void SetStatus(string message)
    {
        var target = SettingsPage.IsVisible ? SettingsStatus : ContextOverlay.IsVisible ? ContextStatus : StatusLabel;
        target.Text = message;
        target.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    private void ShowFailure(HarnessException ex) => ShowNotice("Couldn’t complete that", $"{ex.Message}\n\n{ex.Hint}");

    private void ShowNotice(string title, string message, string confirmText = "OK", Action? confirm = null)
    {
        noticeReturnFocus = FocusManager?.GetFocusedElement() as Control;
        noticeAction = confirm;
        NoticeTitle.Text = title;
        NoticeMessage.Text = message;
        NoticeConfirmButton.Content = confirmText;
        NoticeCancelButton.IsVisible = confirm is not null;
        MainSurface.IsEnabled = false;
        SettingsPage.IsEnabled = false;
        ContextOverlay.IsEnabled = false;
        NoticeOverlay.IsVisible = true;
        if (confirm is not null) NoticeCancelButton.Focus();
        else NoticeConfirmButton.Focus();
    }

    private void DismissNotice_Click(object? sender, RoutedEventArgs e) => DismissNotice();

    private void ConfirmNotice_Click(object? sender, RoutedEventArgs e)
    {
        var action = noticeAction;
        DismissNotice();
        action?.Invoke();
    }

    private void DismissNotice()
    {
        NoticeOverlay.IsVisible = false;
        MainSurface.IsEnabled = !ContextOverlay.IsVisible;
        SettingsPage.IsEnabled = true;
        ContextOverlay.IsEnabled = true;
        noticeAction = null;
        noticeReturnFocus?.Focus();
        noticeReturnFocus = null;
    }

    private void RequireConnection()
    {
        if (session is null) throw new HarnessException(ErrorCode.Configuration, "Apply an AI connection first.", "Select your provider, model or trusted policy.");
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => operation?.Cancel();

    private async void Prompt_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            await SendAsync();
        }
    }

    private void SuggestSummary_Click(object? sender, RoutedEventArgs e) => PromptBox.Text = "Summarize the attached context, cite your sources, and list the next steps.";
    private void SuggestGaps_Click(object? sender, RoutedEventArgs e) => PromptBox.Text = "Compare the attached change with the ticket and specifications. Identify requirement gaps and explain what cannot be verified.";
    private void SuggestDraft_Click(object? sender, RoutedEventArgs e) => PromptBox.Text = "Draft a concise response for the attached PR or ticket, grounded in the supplied evidence.";
}
