using BusinessHarness.Core;
using BusinessHarness.Infrastructure;
using Microsoft.Identity.Client.Extensions.Msal;
using System.Security.Cryptography;
using System.Text;

namespace BusinessHarness.Desktop;

/// <summary>OS-protected per-user key; never enables unencrypted fallback.</summary>
public sealed class DesktopOsKeyVault(string directory) : IDesktopSettingsKeyVault
{
    public static string KeyIdentity(string directory)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
    }

    private Storage Create()
    {
        SafePaths.RejectLinks(Path.Combine(directory, "settings-key.dat"));
        var identity = KeyIdentity(directory);
        var properties = new StorageCreationPropertiesBuilder("settings-key.dat", directory)
            .WithMacKeyChain("net.businessharness.desktop.settings", "settings-" + identity)
            .WithLinuxKeyring("net.businessharness.desktop.settings", MsalCacheHelper.LinuxKeyRingDefaultCollection,
                "BusinessHarness settings key", new("instance", identity), new("purpose", "settings-key"))
            .Build();
        return Storage.Create(properties, null);
    }

    public byte[]? ReadKey()
    {
        try { return Create().ReadData(); }
        catch (Exception) { throw Unavailable(); }
    }

    public void WriteKey(byte[] key)
    {
        try { Create().WriteData(key); }
        catch (Exception) { throw Unavailable(); }
    }

    public void DeleteKey()
    {
        try { Create().Clear(false); }
        catch (Exception) { throw Unavailable(); }
    }

    private static HarnessException Unavailable() => new(ErrorCode.FileSystem, "The OS credential store is unavailable.",
        "Unlock your OS keychain or keyring and try again. Settings are not saved in plaintext.");
}

