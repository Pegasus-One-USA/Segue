using System.Text;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using Renci.SshNet;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Delivers mapped records as an NDJSON file over SFTP. The destination secret is an SFTP URI of the form
/// <c>sftp://user:password@host:port/remote/dir</c>. The file is named per the mapping profile and uploaded to the
/// remote directory, creating it if needed.
/// </summary>
public sealed class MappedSftpDestinationWriter : IConfiguredDestinationWriter
{
    private readonly ISecretProvider _secretProvider;

    public MappedSftpDestinationWriter(ISecretProvider secretProvider)
    {
        _secretProvider = secretProvider;
    }

    public async Task<int> WriteAsync(
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return 0;
        }

        var secret = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
        if (!Uri.TryCreate(secret, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "sftp", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("SFTP destination secret must be an 'sftp://user:password@host:port/path' URI.");
        }

        var userInfo = uri.UserInfo.Split(':', 2);
        var username = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
        var port = uri.IsDefaultPort ? 22 : uri.Port;
        var remoteDir = string.IsNullOrWhiteSpace(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
        var fileName = MappedDestinationSerialization.BuildFileName(destination, mappingProfile, "ndjson");
        var content = MappedDestinationSerialization.ToNdjson(records);

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

        return records.Count;
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
