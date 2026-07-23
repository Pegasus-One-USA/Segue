using System.Text;
using Renci.SshNet;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Shared SFTP connect/ensure-directory/upload logic, extracted from <see cref="MappedSftpDestinationWriter"/> so
/// it can also back <see cref="Delivery.SftpDeliveryStrategy"/>'s CSV-over-SFTP delivery without duplicating the
/// SSH.NET plumbing. Callers supply already-serialized content and a filename — this class does not care about format.
/// </summary>
internal static class SftpUploader
{
    public static async Task UploadAsync(
        string sftpSecretUri,
        string fileName,
        string content,
        CancellationToken cancellationToken)
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
        await Task.Run(() =>
        {
            client.Connect();
            try
            {
                EnsureRemoteDirectory(client, remoteDir);
                var remotePath = $"{remoteDir.TrimEnd('/')}/{fileName}";
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
                client.UploadFile(stream, remotePath, canOverride: true);
            }
            finally
            {
                client.Disconnect();
            }
        }, cancellationToken);
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
