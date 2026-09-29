using System.Diagnostics;
using System.Text;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

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

    public async Task<DestinationWriteResult> WriteAsync(
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        PipelineWriteContext context,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return new DestinationWriteResult(0);
        }

        var secret = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
        var fileName = MappedDestinationSerialization.BuildFileName(destination, mappingProfile, "ndjson");
        var content = Encoding.UTF8.GetBytes(MappedDestinationSerialization.ToNdjson(records));

        // Reported from the uploader's own connect hook rather than around this whole call, so the Connect stage
        // reflects the SSH connection alone — not the directory creation and file transfer that follow it.
        // A connect that fails throws out of UploadAsync before the hook runs, so the Failed line comes from the
        // decorator's Complete stage carrying the SSH error; that is the single failure row the design calls for.
        var connectStartedAt = Stopwatch.GetTimestamp();
        await SftpUploader.UploadAsync(
            secret, fileName, content, cancellationToken,
            onConnectedAsync: () => context.ReportConnectedAsync(connectStartedAt, cancellationToken));

        return new DestinationWriteResult(records.Count);
    }
}
