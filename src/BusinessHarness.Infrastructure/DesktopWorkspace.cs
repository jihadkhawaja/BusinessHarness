using System.Text.Json;
using BusinessHarness.Application;
using BusinessHarness.Core;

namespace BusinessHarness.Infrastructure;

/// <summary>Desktop composition: explicit trusted policy and user-selected reads only.</summary>
public sealed class DesktopWorkspace(HttpClient client, Func<string, string?> environment)
{
    public HarnessPolicy Policy { get; private set; } = new();
    private SecretRedactor redactor = new();
    private HashSet<string> knownSecrets = new(StringComparer.Ordinal);
    private DirectAtlassianConnection? atlassian;

    public bool HasAtlassian => atlassian is not null;
    public string? AtlassianSite => atlassian?.Site;

    public void ConnectAtlassian(string site, string email, string token)
    {
        var connection = new DirectAtlassianConnection(client, site, email, token, () => redactor);
        UpdateConnectionSecrets(connection.Secrets);
        atlassian = connection;
    }

    public void DisconnectAtlassian() => atlassian = null;

    public async Task<ChatAttachment> ReadAtlassianAsync(string kind, string identifier, CancellationToken ct)
    {
        var connection = atlassian ?? throw new HarnessException(ErrorCode.Configuration, "Connect Jira first.", "Add your Jira site, email and API token in Settings.");
        var document = await connection.ReadAsync(kind, identifier, ct);
        return Protect(new(document.Key, JsonSerializer.Serialize(document, JsonDefaults.Options)));
    }

    public void UpdateConnectionSecrets(IEnumerable<string> values)
    {
        var updated = new HashSet<string>(knownSecrets, StringComparer.Ordinal);
        updated.UnionWith(values.Where(s => !string.IsNullOrEmpty(s)));
        if (updated.Count > 256 || updated.Sum(s => (long)s.Length) > 128_000)
            throw new HarnessException(ErrorCode.LimitExceeded, "Too many credentials were used in this session.", "Restart the app before adding another credential.");
        var updatedRedactor = new SecretRedactor(updated);
        knownSecrets = updated;
        redactor = updatedRedactor;
    }
    public ChatAttachment Protect(ChatAttachment attachment) => new(redactor.Redact(attachment.Name), redactor.Redact(attachment.Content));

    public async Task ConfigureAsync(string? policyPath, string provider, string? model, CancellationToken ct)
    {
        var policy = new HarnessPolicy();
        if (!string.IsNullOrWhiteSpace(policyPath))
        {
            SafePaths.RejectLinks(policyPath);
            await using var stream = new FileStream(policyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 64_000)
                throw new HarnessException(ErrorCode.Configuration, "Policy exceeds 64 KB.", "Select a small trusted policy outside the reviewed branch.");
            policy = await JsonSerializer.DeserializeAsync<HarnessPolicy>(stream, JsonDefaults.Options, ct) ??
                throw new HarnessException(ErrorCode.Configuration, "Policy is empty.", "Select a trusted JSON policy object.");
        }
        policy = policy with { Provider = provider, Model = string.IsNullOrWhiteSpace(model) ? policy.Model : model.Trim() };
        policy.Validate();
        ConnectorReferenceRequests.ValidateSources(policy.ReferenceSources);
        var names = new[] { "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "OPENAI_CHATGPT_ACCESS_TOKEN", "JIRA_API_TOKEN", "GITHUB_TOKEN", "GITLAB_TOKEN", "JIRA_EMAIL" }
            .Concat(policy.ReferenceSources.Select(s => s.TokenEnvironment).OfType<string>())
            .Concat(policy.ChangeRequest?.TokenEnvironment is { } hostSecret ? [hostSecret] : []);
        var secrets = names.Distinct(StringComparer.Ordinal).Select(environment).OfType<string>().ToList();
        if (environment("JIRA_EMAIL") is { } email && environment("JIRA_API_TOKEN") is { } token)
            secrets.Add(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(email + ":" + token)));
        UpdateConnectionSecrets(secrets);
        Policy = policy;
    }

    public ChatSession CreateChat(bool chatGptPlan)
    {
        var name = chatGptPlan ? "OPENAI_CHATGPT_ACCESS_TOKEN" : Policy.Provider == "openai" ? "OPENAI_API_KEY" : "ANTHROPIC_API_KEY";
        var credential = IntegrationSecrets.Require(environment, name);
        return new(new ApiChatProvider(client, Policy, credential, redactor, chatGptPlan, () => redactor));
    }

    public async Task<IReadOnlyList<ChatAttachment>> ReadReferencesAsync(string identifiers, CancellationToken ct)
    {
        var selected = identifiers.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (selected.Length is < 1 or > 20)
            throw new HarnessException(ErrorCode.Configuration, "Select 1–20 references.", "Use Jira keys or configured SOURCE:ID values, separated by commas.");
        var documents = await Resolver().ResolveAsync("", selected, ct);
        return documents.Select(d => new ChatAttachment(d.Key, redactor.Redact(JsonSerializer.Serialize(d)))).ToArray();
    }

    public Task<IReadOnlyList<ChatAttachment>> ReadChangeRequestAsync(CancellationToken ct) => ReadChangeRequestAsync(null, ct);

    public async Task<IReadOnlyList<ChatAttachment>> ReadChangeRequestAsync(string? url, CancellationToken ct)
    {
        var host = (string.IsNullOrWhiteSpace(url) ? Policy.ChangeRequest : ParseChangeRequest(url))
            ?? throw new HarnessException(ErrorCode.Configuration,
                "No pull request is configured.", "Set changeRequest in the explicit trusted policy; GitHub and GitLab are supported.");
        var token = IntegrationSecrets.Require(environment, host.TokenEnvironment ?? (host.Platform == "github" ? "GITHUB_TOKEN" : "GITLAB_TOKEN"));
        var metadata = await new ChangeRequestHost(new(client), host, token, redactor).ReadAsync(ct);
        var documents = await Resolver().ResolveAsync(metadata.Title + "\n" + metadata.Description, [], ct);
        return [new($"{host.Platform}:{host.Project}#{host.Number}", redactor.Redact(JsonSerializer.Serialize(metadata))),
            .. documents.Select(d => new ChatAttachment(d.Key, redactor.Redact(JsonSerializer.Serialize(d))))];
    }

    public static ChangeRequestPolicy ParseChangeRequest(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) throw InvalidChange();
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        ChangeRequestPolicy policy;
        if (uri.Host == "github.com" && parts.Length == 4 && parts[2] == "pull" && int.TryParse(parts[3], out var githubNumber))
            policy = new() { Project = parts[0] + "/" + parts[1], Number = githubNumber };
        else if (uri.Host == "gitlab.com" && parts.Length >= 5 && parts[^3] == "-" && parts[^2] == "merge_requests" && int.TryParse(parts[^1], out var gitlabNumber))
            policy = new() { Platform = "gitlab", Project = string.Join('/', parts[..^3]), Number = gitlabNumber };
        else throw InvalidChange();
        policy.Validate(requireIdentity: true);
        return policy;
    }

    private static HarnessException InvalidChange() => new(ErrorCode.Configuration, "Enter a GitHub or GitLab pull request URL.", "Use a public github.com/pull or gitlab.com/merge_requests URL. Self-hosted instances use trusted policy.");

    public async Task<ChatAttachment> ReadDiffAsync(string repository, string baseRef, string headRef, CancellationToken ct)
    {
        var changes = await new GitRepository(repository, Policy, redactor).ReadChangesAsync(baseRef, headRef, ct);
        // Excluded paths are not sent to chat; retain a count so evidence gaps stay explicit.
        var content = JsonSerializer.Serialize(new
        {
            changes.BaseCommit,
            changes.HeadCommit,
            changes.Files,
            withheldFileCount = changes.ExcludedFiles.Count,
            localFindings = changes.SecurityFindings.Select(f => new { f.RuleId, f.Severity, f.Line, f.Title })
        });
        return new($"Diff {changes.BaseCommit[..12]}..{changes.HeadCommit[..12]}", redactor.Redact(content));
    }

    private ReferenceResolver Resolver() => new(new(client), Policy, environment, redactor,
        Policy.JiraSite is null ? null : new JiraIssueSource(new(client), Policy,
            IntegrationSecrets.Require(environment, "JIRA_EMAIL"), IntegrationSecrets.Require(environment, "JIRA_API_TOKEN"), redactor));
}
