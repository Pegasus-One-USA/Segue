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
        var content = MappedDestinationSerialization.ToNdjson(records);

        await SftpUploader.UploadAsync(secret, fileName, content, cancellationToken);

        return new DestinationWriteResult(records.Count);
    }
}
