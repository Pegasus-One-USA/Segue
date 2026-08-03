using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations.Delivery;

/// <summary>
/// Uploads the generated file to the destination's SFTP secret (<c>sftp://user:password@host:port/path</c>), reusing
/// the same <see cref="SftpUploader"/> helper as <see cref="MappedSftpDestinationWriter"/> — the only difference is
/// the content/filename come from an already-serialized <see cref="GeneratedFile"/> instead of raw NDJSON.
/// </summary>
public sealed class SftpDeliveryStrategy : IArtifactDeliveryStrategy
{
    private readonly ISecretProvider _secretProvider;

    public SftpDeliveryStrategy(ISecretProvider secretProvider)
    {
        _secretProvider = secretProvider;
    }

    public async Task<DestinationWriteResult> DeliverAsync(
        DestinationConfiguration destination,
        GeneratedFile file,
        int recordCount,
        PipelineWriteContext context,
        CancellationToken cancellationToken)
    {
        var secret = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
        await SftpUploader.UploadAsync(secret, file.FileName, file.Content, cancellationToken);

        return new DestinationWriteResult(recordCount);
    }
}
