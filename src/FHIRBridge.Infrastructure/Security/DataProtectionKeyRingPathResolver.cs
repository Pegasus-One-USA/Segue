namespace FHIRBridge.Infrastructure.Security;

/// <summary>The outcome of <see cref="DataProtectionKeyRingPathResolver.Resolve"/>.</summary>
/// <param name="Path">Directory the Data Protection key ring should be persisted to.</param>
/// <param name="WasConfigured">True when <c>DataProtection:KeyRingPath</c> supplied the path explicitly.</param>
/// <param name="Warning">Non-null when the resolved path is identity-scoped and therefore fragile — the caller
/// should surface this prominently at startup.</param>
public sealed record KeyRingPathResolution(string Path, bool WasConfigured, string? Warning);

/// <summary>
/// Resolves the Data Protection key ring directory shared by the Api and Worker hosts. Both hosts call this
/// (rather than each computing a path inline) because they MUST land on the same directory: the ring protects
/// the <c>ProvisionedSecrets</c> rows holding the JWT signing key, download-link secret, transform hashing key
/// and — most destructively — the AES key behind <see cref="Application.Abstractions.Security.IPhiFieldEncryptor"/>.
/// Two hosts on two rings each treat the other's rows as unreadable and regenerate them, which permanently
/// orphans every already-encrypted execution-history payload.
/// <para>
/// The unconfigured fallback is deliberately MACHINE-wide (<c>CommonApplicationData</c> — <c>C:\ProgramData</c>
/// on Windows) rather than per-user (<c>LocalApplicationData</c>): a Windows service running as LocalSystem, the
/// same exe launched by a deploy script under its own account, and a host whose service identity is changed later
/// all resolve one ring instead of one ring each. A containerized deployment sets
/// <c>DataProtection:KeyRingPath</c> explicitly (to a mounted volume), so the fallback never applies there.
/// </para>
/// </summary>
public static class DataProtectionKeyRingPathResolver
{
    private const string DirectoryName = "dataprotection-keys";

    public static KeyRingPathResolution Resolve(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return new KeyRingPathResolution(configuredPath.Trim(), true, null);
        }

        // Not every platform reports CommonApplicationData (and on Linux it is /usr/share, which a non-root
        // service account usually cannot write). An empty value would make Path.Combine return a path relative
        // to the current working directory — worse than the per-user fallback — so treat it as unavailable.
        var machineWideRoot = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(machineWideRoot))
        {
            var machineWidePath = Path.Combine(machineWideRoot, "FHIRBridge", DirectoryName);
            if (TryEnsureWritable(machineWidePath))
            {
                return new KeyRingPathResolution(machineWidePath, false, null);
            }
        }

        // Last resort: the historical per-user location. Still a persistent ring (never ephemeral), but it is
        // scoped to whichever account the process runs as, so it only holds together while every host and every
        // out-of-band launch of these binaries uses the same identity.
        var perUserPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FHIRBridge",
            DirectoryName);

        return new KeyRingPathResolution(
            perUserPath,
            false,
            $"DataProtection:KeyRingPath is not set and the machine-wide key ring location is not writable; " +
            $"falling back to the per-user ring at '{perUserPath}'. This path depends on the account this process " +
            "runs as — if the Api and Worker run as different accounts they will each regenerate the app secrets " +
            "and permanently orphan previously encrypted execution-history payloads. Set " +
            "DataProtection:KeyRingPath to a shared, persistent path.");
    }

    // Creating the directory is not proof of write access (an existing directory owned by another account can be
    // listable but not writable), so probe with a real file the way the key ring itself would.
    private static bool TryEnsureWritable(string path)
    {
        try
        {
            Directory.CreateDirectory(path);

            var probePath = Path.Combine(path, $".write-probe-{Guid.NewGuid():N}");
            using (var probe = File.Create(probePath, 1, FileOptions.DeleteOnClose))
            {
                probe.WriteByte(0);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
