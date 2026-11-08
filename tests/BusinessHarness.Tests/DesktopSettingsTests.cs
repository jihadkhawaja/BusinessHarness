using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BusinessHarness.Core;
using BusinessHarness.Desktop;
using BusinessHarness.Infrastructure;
using Xunit;

namespace BusinessHarness.Tests;

public sealed class DesktopSettingsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static DesktopSettings Settings => new(0, "model", "", "https://team.atlassian.net/", "person@example.invalid",
        new(StringComparer.Ordinal) { ["OPENAI_API_KEY"] = "synthetic-ai-secret", ["JIRA_API_TOKEN"] = "synthetic-jira-secret", ["GITHUB_TOKEN"] = "synthetic-github-secret" });

    [Fact]
    public void VaultIdentityIsStablePerDirectoryAndDistinctAcrossDirectories()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        Assert.Equal(DesktopOsKeyVault.KeyIdentity(first.Path), DesktopOsKeyVault.KeyIdentity(first.Path + Path.DirectorySeparatorChar));
        Assert.NotEqual(DesktopOsKeyVault.KeyIdentity(first.Path), DesktopOsKeyVault.KeyIdentity(second.Path));
        Assert.DoesNotContain(first.Path, DesktopOsKeyVault.KeyIdentity(first.Path));
    }

    [Fact]
    public void PendingAppFieldsMustBeAppliedBeforeSaving()
    {
        var applied = new Dictionary<string, string> { ["GITHUB_TOKEN"] = "first" };
        DesktopSettingsDraft.EnsureConnectionsApplied(applied, "first", "", false, "", "", "");
        Assert.Throws<HarnessException>(() => DesktopSettingsDraft.EnsureConnectionsApplied(applied, "replacement", "", false, "", "", ""));
        Assert.Throws<HarnessException>(() => DesktopSettingsDraft.EnsureConnectionsApplied(applied, "", "gitlab", false, "", "", ""));
        Assert.Throws<HarnessException>(() => DesktopSettingsDraft.EnsureConnectionsApplied(applied, "", "", false, "https://team.atlassian.net/", "", ""));
    }

    [Fact]
    public void PendingCatalogCredentialsOrInstanceUrlMustBeAppliedBeforeSaving()
    {
        var current = new DesktopConnectorSettings("https://team.example.com/", "token");
        DesktopSettingsDraft.EnsureCatalogApplied(current, true, "https://team.example.com/", "");
        Assert.Throws<HarnessException>(() => DesktopSettingsDraft.EnsureCatalogApplied(current, true, "https://other.example.com/", ""));
        Assert.Throws<HarnessException>(() => DesktopSettingsDraft.EnsureCatalogApplied(current, true, "", ""));
        Assert.Throws<HarnessException>(() => DesktopSettingsDraft.EnsureCatalogApplied(current, true, null, ""));
        Assert.Throws<HarnessException>(() => DesktopSettingsDraft.EnsureCatalogApplied(current, true, "https://team.example.com/", "replacement"));
        Assert.Throws<HarnessException>(() => DesktopSettingsDraft.EnsureCatalogApplied(null, true, "https://team.example.com/", ""));
    }

    [Theory]
    [InlineData("JIRA_API_TOKEN")]
    [InlineData("DIRECT_JIRA_API_TOKEN")]
    public void SavedPersonalJiraTokenNeverBecomesPolicyEnvironmentSecret(string tokenName)
    {
        var stored = new Dictionary<string, string> { [tokenName] = "personal-token", ["GITHUB_TOKEN"] = "github-token" };
        Assert.Equal("personal-token", DesktopSettingsDraft.DirectJiraToken(stored));
        var runtime = DesktopSettingsDraft.RuntimeSecrets(stored);
        Assert.False(runtime.ContainsKey("JIRA_API_TOKEN"));
        Assert.False(runtime.ContainsKey("DIRECT_JIRA_API_TOKEN"));
        Assert.Equal("github-token", runtime["GITHUB_TOKEN"]);
    }

    [Fact]
    public async Task SettingsRoundTripEncryptsEveryFieldAndUsesFreshNonce()
    {
        using var directory = new TempDirectory();
        var vault = new FakeVault();
        var store = new DesktopSettingsStore(directory.Path, vault);
        await store.SaveAsync(Settings, Ct);
        var path = Path.Combine(directory.Path, "settings.enc");
        var first = await File.ReadAllBytesAsync(path, Ct);
        var encoded = Encoding.UTF8.GetString(first);
        Assert.DoesNotContain("synthetic", encoded);
        Assert.DoesNotContain("person@example.invalid", encoded);
        Assert.Equal(32, vault.Key!.Length);
        var loaded = await new DesktopSettingsStore(directory.Path, vault).LoadAsync(Ct);
        Assert.NotNull(loaded);
        Assert.Equal(Settings.JiraSite, loaded.JiraSite);
        Assert.Equal(Settings.Secrets["GITHUB_TOKEN"], loaded.Secrets["GITHUB_TOKEN"]);
        await store.SaveAsync(Settings, Ct);
        Assert.NotEqual(first, await File.ReadAllBytesAsync(path, Ct));
    }

    [Fact]
    public async Task SavedCatalogConnectionsRemainEncryptedAndBoundToTheirProvider()
    {
        using var directory = new TempDirectory();
        var vault = new FakeVault();
        var store = new DesktopSettingsStore(directory.Path, vault);
        var settings = Settings with { Connectors = new() { ["notion"] = new("", "notion-private-value"), ["asana"] = new("", "asana-private-value") } };
        await store.SaveAsync(settings, Ct);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(directory.Path, "settings.enc"), Ct);
        Assert.DoesNotContain("private-value", Encoding.UTF8.GetString(bytes));
        var restored = await store.LoadAsync(Ct);
        Assert.Equal("notion-private-value", restored!.Connectors!["notion"].Credential);
        Assert.Equal("asana-private-value", restored.Connectors["asana"].Credential);
        var invalid = settings with { Connectors = new() { ["unknown-provider"] = new("", "value") } };
        await Assert.ThrowsAsync<HarnessException>(() => store.SaveAsync(invalid, Ct));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(directory.Path, "settings.enc"), Ct));
    }

    [Fact]
    public async Task PreviouslySavedSettingsRestoreWithoutConnectorProperty()
    {
        using var directory = new TempDirectory();
        var key = RandomNumberGenerator.GetBytes(32);
        var vault = new FakeVault { Key = key.ToArray() };
        var plain = JsonSerializer.SerializeToUtf8Bytes(new
        {
            provider = 0,
            model = "test-model",
            policyPath = "",
            jiraSite = "https://team.atlassian.net/",
            jiraEmail = "person@example.invalid",
            secrets = new Dictionary<string, string> { ["JIRA_API_TOKEN"] = "legacy-jira-token" }
        });
        var header = "BHSET01"u8.ToArray();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(key, tag.Length)) aes.Encrypt(nonce, plain, cipher, tag, header);
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "settings.enc"), [.. header, .. nonce, .. tag, .. cipher], Ct);
        var restored = await new DesktopSettingsStore(directory.Path, vault).LoadAsync(Ct);
        Assert.NotNull(restored);
        Assert.Null(restored.Connectors);
        Assert.Equal("legacy-jira-token", DesktopSettingsDraft.DirectJiraToken(restored.Secrets));
        Assert.DoesNotContain("JIRA_API_TOKEN", DesktopSettingsDraft.RuntimeSecrets(restored.Secrets).Keys);
    }

    [Fact]
    public async Task TamperingWrongKeyAndMissingKeyFailClosed()
    {
        using var directory = new TempDirectory();
        var vault = new FakeVault();
        var store = new DesktopSettingsStore(directory.Path, vault);
        await store.SaveAsync(Settings, Ct);
        var path = Path.Combine(directory.Path, "settings.enc");
        var original = await File.ReadAllBytesAsync(path, Ct);
        var damaged = original.ToArray();
        damaged[^1] ^= 1;
        await File.WriteAllBytesAsync(path, damaged, Ct);
        await Assert.ThrowsAsync<HarnessException>(() => store.LoadAsync(Ct));
        await Assert.ThrowsAsync<HarnessException>(() => store.SaveAsync(Settings, Ct));
        Assert.Equal(damaged, await File.ReadAllBytesAsync(path, Ct));
        await File.WriteAllBytesAsync(path, original, Ct);
        var wrong = new FakeVault { Key = RandomNumberGenerator.GetBytes(32) };
        await Assert.ThrowsAsync<HarnessException>(() => new DesktopSettingsStore(directory.Path, wrong).LoadAsync(Ct));
        await Assert.ThrowsAsync<HarnessException>(() => new DesktopSettingsStore(directory.Path, wrong).SaveAsync(Settings, Ct));
        Assert.Equal(original, await File.ReadAllBytesAsync(path, Ct));
        var missing = new FakeVault();
        await Assert.ThrowsAsync<HarnessException>(() => new DesktopSettingsStore(directory.Path, missing).SaveAsync(Settings, Ct));
        Assert.Null(missing.Key);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, Ct));
    }

    [Fact]
    public async Task VaultFailureAndUnrecognizedSecretsNeverWritePlaintext()
    {
        using var directory = new TempDirectory();
        var vault = new FakeVault { Fail = true };
        var store = new DesktopSettingsStore(directory.Path, vault);
        await Assert.ThrowsAsync<HarnessException>(() => store.SaveAsync(Settings, Ct));
        Assert.False(File.Exists(Path.Combine(directory.Path, "settings.enc")));
        var invalid = Settings with { Secrets = new() { ["UNKNOWN_HOST_TOKEN"] = "secret" } };
        await Assert.ThrowsAsync<HarnessException>(() => store.SaveAsync(invalid, Ct));
        Assert.False(File.Exists(Path.Combine(directory.Path, "settings.enc")));
    }

    [Fact]
    public async Task LockedVaultOnRestoreReturnsSafeFailure()
    {
        using var directory = new TempDirectory();
        var vault = new FakeVault();
        var store = new DesktopSettingsStore(directory.Path, vault);
        await store.SaveAsync(Settings, Ct);
        vault.Fail = true;
        var failure = await Assert.ThrowsAsync<HarnessException>(() => store.LoadAsync(Ct));
        Assert.DoesNotContain("Unavailable", failure.Message, StringComparison.Ordinal);
        Assert.Contains("credential store", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForgetRemovesCiphertextAndVaultKey()
    {
        using var directory = new TempDirectory();
        var vault = new FakeVault();
        var store = new DesktopSettingsStore(directory.Path, vault);
        await store.SaveAsync(Settings, Ct);
        await store.ClearAsync(Ct);
        Assert.Null(vault.Key);
        Assert.False(File.Exists(Path.Combine(directory.Path, "settings.enc")));
        Assert.Null(await store.LoadAsync(Ct));
    }

    [Fact]
    public async Task WindowsUserVaultRoundTripUsesProtectedKeyFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory();
        var store = new DesktopSettingsStore(directory.Path, new DesktopOsKeyVault(directory.Path));
        await store.SaveAsync(Settings, Ct);
        var restored = await new DesktopSettingsStore(directory.Path, new DesktopOsKeyVault(directory.Path)).LoadAsync(Ct);
        Assert.NotNull(restored);
        Assert.Equal("synthetic-jira-secret", restored.Secrets["JIRA_API_TOKEN"]);
        var keyFile = await File.ReadAllBytesAsync(Path.Combine(directory.Path, "settings-key.dat"), Ct);
        Assert.DoesNotContain("synthetic", Encoding.UTF8.GetString(keyFile));
        await store.ClearAsync(Ct);
        Assert.False(File.Exists(Path.Combine(directory.Path, "settings-key.dat")));
        Assert.False(File.Exists(Path.Combine(directory.Path, "settings.enc")));
    }

    private sealed class FakeVault : IDesktopSettingsKeyVault
    {
        public byte[]? Key { get; set; }
        public bool Fail { get; set; }
        public byte[]? ReadKey() => Fail ? throw new InvalidOperationException("Unavailable") : Key?.ToArray();
        public void WriteKey(byte[] key)
        {
            if (Fail) throw new InvalidOperationException("Unavailable");
            Key = key.ToArray();
        }
        public void DeleteKey()
        {
            if (Fail) throw new InvalidOperationException("Unavailable");
            Key = null;
        }
    }
}
