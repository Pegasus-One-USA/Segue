using System.Text;
using Azure;
using Azure.Storage.Blobs.Models;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Destinations.Blob;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// First-class writer for the <c>BlobStorage</c> destination, backed by the real Azure.Storage.Blobs SDK (see
/// <see cref="IBlobContainerClientFactory"/> for the auth-mode dispatch) rather than the generic
/// pre-signed-URL-PUT convention <see cref="MappedDestinationSerialization.WriteTextTargetAsync"/> still serves
/// for S3/NDJSON/etc. <see cref="DestinationConfiguration.Target"/> is the container name here, not a file stem,
/// so the blob name is built from the mapping profile's destination object instead of
/// <see cref="MappedDestinationSerialization.BuildFileName"/>.
/// </summary>
public sealed class MappedBlobStorageDestinationWriter : IConfiguredDestinationWriter
{
    private readonly IBlobContainerClientFactory _clientFactory;
    private readonly ILogger<MappedBlobStorageDestinationWriter> _logger;

    public MappedBlobStorageDestinationWriter(
        IBlobContainerClientFactory clientFactory, ILogger<MappedBlobStorageDestinationWriter> logger)
    {
        _clientFactory = clientFactory;
        _logger = logger;
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

        var settings = BlobDestinationSettings.Parse(destination);
        var target = await _clientFactory.GetTargetAsync(destination, settings, cancellationToken);

        if (settings.CreateContainerIfNotExists && target.SupportsContainerCreate)
        {
            try
            {
                // Fully positional and 3-arg on purpose: BlobContainerClient overloads CreateIfNotExistsAsync with
                // an extra encryptionScopeOptions parameter, and named/omitted arguments make that overload
                // ambiguous with this one — an exact positional match pins it to the 3-parameter overload.
                await target.Container.CreateIfNotExistsAsync(PublicAccessType.None, null, cancellationToken);
            }
            catch (RequestFailedException exception) when (exception.Status is 403 or 409)
            {
                // Most commonly the identity is scoped to write inside an already-existing container (e.g.
                // "Storage Blob Data Contributor" on the container, not the account) — the upload below will
                // surface the real error if the container genuinely doesn't exist.
                _logger.LogWarning(
                    exception,
                    "Could not create container '{Container}' for destination {DestinationId} — continuing, assuming it already exists.",
                    settings.ContainerName,
                    destination.Id);
            }
        }

        var blobName = BuildBlobName(mappingProfile, settings);
        var blobClient = target.Container.GetBlobClient(blobName);
        var content = MappedDestinationSerialization.ToNdjson(records);

        using var payload = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await blobClient.UploadAsync(
            payload,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/x-ndjson" },
                Metadata = BuildMetadata(mappingProfile, records, context),
                // The cast is load-bearing: without it, the ternary's null branch resolves through AccessTier's
                // implicit string conversion instead of a plain null, and throws on a null settings.AccessTier.
                AccessTier = settings.AccessTier is null ? null : (AccessTier?)new AccessTier(settings.AccessTier),
            },
            cancellationToken);

        return new DestinationWriteResult(records.Count);
    }

    private static string BuildBlobName(MappingProfile mappingProfile, BlobDestinationSettings settings)
    {
        var cleanStem = string.Join(
            "_", mappingProfile.DestinationObject.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var fileName = $"{cleanStem}_{DateTime.UtcNow:yyyyMMddHHmmssfff}.ndjson";

        return string.IsNullOrWhiteSpace(settings.PathPrefix) ? fileName : $"{settings.PathPrefix}/{fileName}";
    }

    private static Dictionary<string, string> BuildMetadata(
        MappingProfile mappingProfile, IReadOnlyCollection<MappedDestinationRecord> records, PipelineWriteContext context)
    {
        var first = records.First();

        return new Dictionary<string, string>
        {
            ["pipelineRunId"] = first.PipelineRunId.ToString(),
            ["resourceType"] = mappingProfile.ResourceType,
            ["destinationObject"] = mappingProfile.DestinationObject,
            ["recordCount"] = records.Count.ToString(),
            ["writtenOnUtc"] = DateTime.UtcNow.ToString("o"),
            // Blob metadata values must be ASCII/header-safe; a user-typed workflow name is not guaranteed to be.
            ["routeName"] = SanitizeAscii(context.RouteName),
        };
    }

    private static string SanitizeAscii(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(character is >= (char)32 and < (char)127 ? character : '_');
        }

        return builder.ToString();
    }
}
