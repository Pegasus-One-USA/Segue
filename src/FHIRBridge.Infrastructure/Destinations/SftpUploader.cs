using Renci.SshNet;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Shared SFTP connect/ensure-directory/upload logic, extracted from <see cref="MappedSftpDestinationWriter"/> so
/// it can also back <see cref="Delivery.SftpDeliveryStrategy"/>'s CSV-over-SFTP delivery without duplicating the
/// SSH.NET plumbing. Callers supply already-serialized bytes and a filename — this class does not care about format
/// (text or binary, e.g. a ZIP archive).
/// </summary>
internal static class SftpUploader
{
    /// <param name="onConnectedAsync">
    /// Optional hook invoked once the SSH connection is established, before any directory or upload work — lets a
    /// caller report the connect as its own governance stage. The connect happens inside this helper, so a caller
    /// that wrapped <see cref="UploadAsync"/> as a whole would be timing (and attributing) the upload too.
    /// Null for callers that don't report stages, which leaves their behaviour untouched.
    /// </param>
    public static async Task UploadAsync(
        string sftpSecretUri,
        string fileName,
        byte[] content,
        CancellationToken cancellationToken,
        Func<Task>? onConnectedAsync = null)
    {
        if (!Uri.TryCreate(sftpSecretUri, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "sftp", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("SFTP destination secret must be an 'sftp://user:password@host:port/path' URI.");
        }

        var userInfo = uri.UserInfo.Split(':', 2);
        var username = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
        var port = uri.IsDefaultPort ? 22 : uri.Port;
        var remoteDir = string.IsNullOrWhiteSpace(uri.AbsolutePath) ? "/" : uri.AbsolutePath;

        using var client = new SftpClient(uri.Host, port, username, password);

        // Connect separately from the upload below so a caller can observe it on its own: SSH.NET's Connect is
        // synchronous, and it is the call that fails on a bad host, port, username or password — the failures a
        // "could not connect" line is meant to name. Task.Run keeps it off the calling thread, as before.
        await Task.Run(client.Connect, cancellationToken);

        try
        {
            if (onConnectedAsync is not null)
            {
                await onConnectedAsync();
            }

            await Task.Run(() =>
            {
                EnsureRemoteDirectory(client, remoteDir);
                var remotePath = $"{remoteDir.TrimEnd('/')}/{fileName}";
                using var stream = new MemoryStream(content);
                client.UploadFile(stream, remotePath, canOverride: true);
            }, cancellationToken);
        }
        finally
        {
            // Covers the hook as well as the upload: a connected client must be disconnected even if the
            // caller's connect-reporting hook throws on its way through.
            client.Disconnect();
        }
    }

    private static void EnsureRemoteDirectory(SftpClient client, string remoteDir)
    {
        var segments = remoteDir.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var path = string.Empty;
        foreach (var segment in segments)
        {
            path += "/" + segment;
            if (!client.Exists(path))
            {
                client.CreateDirectory(path);
            }
        }
    }
}
