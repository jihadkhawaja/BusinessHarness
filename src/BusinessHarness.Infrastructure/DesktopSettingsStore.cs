using System.Security.Cryptography;
using System.Text.Json;
using BusinessHarness.Core;

namespace BusinessHarness.Infrastructure;

public sealed record DesktopSettings(int Provider, string Model, string PolicyPath, string JiraSite, string JiraEmail,
    Dictionary<string, string> Secrets, Dictionary<string, DesktopConnectorSettings>? Connectors = null);

public static class DesktopSettingsDraft
{
    public static string? DirectJiraToken(IReadOnlyDictionary<string, string> secrets) =>
        secrets.TryGetValue("DIRECT_JIRA_API_TOKEN", out var current) ? current : secrets.GetValueOrDefault("JIRA_API_TOKEN");

    public static Dictionary<string, string> RuntimeSecrets(IReadOnlyDictionary<string, string> secrets) =>
        secrets.Where(pair => pair.Key is not ("DIRECT_JIRA_API_TOKEN" or "JIRA_API_TOKEN"))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    public static void EnsureCatalogApplied(DesktopConnectorSettings? applied, bool requiresBase, string? baseUri, string? credential)
    {
        var draftBase = baseUri?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(credential) ||
            (requiresBase && (applied is null ? draftBase.Length > 0 : applied.BaseUri != draftBase)))
            throw new HarnessException(ErrorCode.Configuration, "Apply your app connection first.", "Choose Use connection, then save settings again.");
    }

    public static void EnsureConnectionsApplied(IReadOnlyDictionary<string, string> applied, string? github, string? gitlab,
        bool jiraConnected, string? jiraSite, string? jiraEmail, string? jiraToken)
    {
        bool Pending(string name, string? value) => !string.IsNullOrWhiteSpace(value) &&
            (!applied.TryGetValue(name, out var saved) || saved != value.Trim());
        if (Pending("GITHUB_TOKEN", github) || Pending("GITLAB_TOKEN", gitlab) ||
            (!jiraConnected && (!string.IsNullOrWhiteSpace(jiraSite) || !string.IsNullOrWhiteSpace(jiraEmail) || !string.IsNullOrWhiteSpace(jiraToken))))
            throw new HarnessException(ErrorCode.Configuration, "Apply your app connection first.",
                "Choose Use token for GitHub or GitLab, or Connect for Jira, then save settings again.");
    }
}

public interface IDesktopSettingsKeyVault
{
    byte[]? ReadKey();
    void WriteKey(byte[] key);
    void DeleteKey();
}

/// <summary>AES-256-GCM settings encrypted with a key protected by the user's OS vault.</summary>
public sealed class DesktopSettingsStore(string directory, IDesktopSettingsKeyVault vault)
{
    private static readonly byte[] Header = "BHSET01"u8.ToArray();
    private static readonly HashSet<string> AllowedSecrets = new(StringComparer.Ordinal)
    {
        "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "OPENAI_CHATGPT_ACCESS_TOKEN", "GITHUB_TOKEN", "GITLAB_TOKEN", "JIRA_API_TOKEN", "DIRECT_JIRA_API_TOKEN"
    };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string file = Path.Combine(directory, "settings.enc");

    private void PrepareDirectory()
    {
        SafePaths.RejectLinks(directory);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        SafePaths.RejectLinks(directory);
        SafePaths.RejectLinks(file);
        SafePaths.RejectLinks(Path.Combine(directory, "settings.lock"));
    }

    public async Task SaveAsync(DesktopSettings settings, CancellationToken ct)
    {
        Validate(settings);
        var plain = JsonSerializer.SerializeToUtf8Bytes(settings, JsonDefaults.Options);
        if (plain.Length > 128_000) throw Invalid();
        await Gate.WaitAsync(ct);
        try
        {
            PrepareDirectory();
            await using var locked = await LockAsync(ct);
            var existing = File.Exists(file);
            var key = ReadVault();
            if (key is null || key.Length == 0)
            {
                if (existing) throw Invalid();
                key = RandomNumberGenerator.GetBytes(32);
                WriteVault(key);
            }
            if (key.Length != 32) throw Invalid();
            try
            {
                if (existing) _ = await ReadAndValidateAsync(key, ct);
                var nonce = RandomNumberGenerator.GetBytes(12);
                var cipher = new byte[plain.Length];
                var tag = new byte[16];
                using (var aes = new AesGcm(key, tag.Length)) aes.Encrypt(nonce, plain, cipher, tag, Header);
                var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    SafePaths.RejectLinks(temp);
                    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
                    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    await using (var stream = new FileStream(temp, options))
                    {
                        await stream.WriteAsync(Header, ct);
                        await stream.WriteAsync(nonce, ct);
                        await stream.WriteAsync(tag, ct);
                        await stream.WriteAsync(cipher, ct);
                        await stream.FlushAsync(ct);
                        stream.Flush(true);
                    }
                    SafePaths.RejectLinks(file);
                    File.Move(temp, file, overwrite: true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); Gate.Release(); }
    }

    public async Task<DesktopSettings?> LoadAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            PrepareDirectory();
            await using var locked = await LockAsync(ct);
            if (!File.Exists(file)) return null;
            var key = ReadVault();
            if (key is null || key.Length != 32) throw Invalid();
            try { return await ReadAndValidateAsync(key, ct); }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        finally { Gate.Release(); }
    }

    public async Task ClearAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            PrepareDirectory();
            await using var locked = await LockAsync(ct);
            try { vault.DeleteKey(); }
            catch (HarnessException) { throw; }
            catch (Exception) { throw VaultUnavailable(); }
            SafePaths.RejectLinks(file);
            if (File.Exists(file)) File.Delete(file);
        }
        finally { Gate.Release(); }
    }

    private async Task<DesktopSettings> ReadAndValidateAsync(byte[] key, CancellationToken ct)
    {
        SafePaths.RejectLinks(file);
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 35 or > 128_035) throw Invalid();
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, ct);
        if (!bytes.AsSpan(0, Header.Length).SequenceEqual(Header)) throw Invalid();
        var plain = new byte[bytes.Length - 35];
        try
        {
            using (var aes = new AesGcm(key, 16)) aes.Decrypt(bytes.AsSpan(7, 12), bytes.AsSpan(35), bytes.AsSpan(19, 16), plain, Header);
            var settings = JsonSerializer.Deserialize<DesktopSettings>(plain, JsonDefaults.Options) ?? throw Invalid();
            Validate(settings);
            return settings;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { throw Invalid(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private async Task<FileStream> LockAsync(CancellationToken ct)
    {
        var path = Path.Combine(directory, "settings.lock");
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            SafePaths.RejectLinks(path);
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None, Options = FileOptions.Asynchronous };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                return new FileStream(path, options);
            }
            catch (IOException) when (attempt < 40) { await Task.Delay(50, ct); }
            catch (IOException) { throw new HarnessException(ErrorCode.FileSystem, "Settings are in use.", "Close the other app instance and try again."); }
        }
    }

    private byte[]? ReadVault()
    {
        try { return vault.ReadKey(); }
        catch (HarnessException) { throw; }
        catch (Exception) { throw VaultUnavailable(); }
    }

    private void WriteVault(byte[] key)
    {
        try { vault.WriteKey(key); }
        catch (HarnessException) { throw; }
        catch (Exception) { throw VaultUnavailable(); }
    }

    private static HarnessException VaultUnavailable() => new(ErrorCode.FileSystem, "The OS credential store is unavailable.",
        "Unlock your OS keychain or keyring and try again. Settings are not saved in plaintext.");

    private static void Validate(DesktopSettings settings)
    {
        if (settings.Provider is < 0 or > 2 || settings.Model is null || settings.Model.Length > 120 || settings.Model.Any(char.IsControl) ||
            settings.PolicyPath is null || settings.PolicyPath.Length > 4096 || settings.PolicyPath.Any(char.IsControl) ||
            settings.JiraSite is null || settings.JiraSite.Length > 2048 || settings.JiraEmail is null || settings.JiraEmail.Length > 320 ||
            settings.Secrets is null || settings.Secrets.Count > AllowedSecrets.Count ||
            settings.Secrets.Any(p => !AllowedSecrets.Contains(p.Key) || string.IsNullOrWhiteSpace(p.Value) || p.Value.Length > 16_000 || p.Value.Any(char.IsControl))) throw Invalid();
        if (settings.JiraSite.Length > 0) _ = JiraIssueSource.ValidateSite(settings.JiraSite);
        if (settings.Secrets.ContainsKey("JIRA_API_TOKEN") && settings.Secrets.ContainsKey("DIRECT_JIRA_API_TOKEN")) throw Invalid();
        var jiraToken = DesktopSettingsDraft.DirectJiraToken(settings.Secrets);
        if ((settings.JiraSite.Length > 0 || settings.JiraEmail.Length > 0 || jiraToken is not null) &&
            (settings.JiraSite.Length == 0 || settings.JiraEmail.Length == 0 || settings.JiraEmail.Contains(':') || settings.JiraEmail.Any(char.IsControl) || jiraToken is null)) throw Invalid();
        if (settings.Connectors is { } connectors)
        {
            if (connectors.Count > 64) throw Invalid();
            foreach (var (provider, connection) in connectors) DesktopConnectorCatalog.ValidateConnection(provider, connection);
        }
    }

    private static HarnessException Invalid() => new(ErrorCode.FileSystem, "Saved settings could not be verified.", "Check the OS credential store and the settings file; no plaintext fallback is used.");
}
