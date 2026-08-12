using System.Globalization;
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
///
/// Two independent settings decide what gets written (see <see cref="BlobDestinationSettings"/>):
/// <see cref="BlobDestinationSettings.Granularity"/> picks <see cref="WriteBulkAsync"/> (the whole batch in one
/// blob) or <see cref="WriteIndividualAsync"/> (one blob per record); when Individual is selected,
/// <see cref="BlobDestinationSettings.RecordMode"/> further decides whether each record's blob is always freshly
/// added (Insert), created-or-overwritten (Upsert), or only ever overwritten if it already exists (Update).
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

        return settings.Granularity == BlobDeliveryGranularity.Individual
            ? await WriteIndividualAsync(target, mappingProfile, records, context, settings, cancellationToken)
            : await WriteBulkAsync(target, mappingProfile, records, context, settings, cancellationToken);
    }

    /// <summary>
    /// Bulk delivery: the whole batch becomes one new timestamped NDJSON blob — object storage's natural
    /// "write-once" shape, suited to a data-lake/audit-trail consumer reading an append-only stream. Every run
    /// produces a new file; there is no per-record identity or "update in place" concept here at all (see
    /// <see cref="WriteIndividualAsync"/> for that).
    /// </summary>
    private static async Task<DestinationWriteResult> WriteBulkAsync(
        BlobDestinationTarget target,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        PipelineWriteContext context,
        BlobDestinationSettings settings,
        CancellationToken cancellationToken)
    {
        var blobName = BuildBulkBlobName(mappingProfile, settings);
        var blobClient = target.Container.GetBlobClient(blobName);
        var content = MappedDestinationSerialization.ToNdjson(records);

        using var payload = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await blobClient.UploadAsync(
            payload,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/x-ndjson" },
                Metadata = BuildBatchMetadata(mappingProfile, records, context),
                // The cast is load-bearing: without it, the ternary's null branch resolves through AccessTier's
                // implicit string conversion instead of a plain null, and throws on a null settings.AccessTier.
                AccessTier = settings.AccessTier is null ? null : (AccessTier?)new AccessTier(settings.AccessTier),
            },
            cancellationToken);

        return new DestinationWriteResult(records.Count);
    }

    /// <summary>
    /// Individual delivery: one blob per record, named by the mapped field flagged <c>IsUpsertKey</c> (falling
    /// back to <see cref="MappedDestinationRecord.SourceResourceId"/>, same fallback
    /// <see cref="MappedSqlServerDestinationWriter"/> uses). <see cref="BlobDestinationSettings.RecordMode"/>
    /// decides what happens relative to whatever's already at that key: <c>Insert</c> always adds a fresh blob
    /// (uniquified so it never collides), <c>Upsert</c> creates-or-overwrites unconditionally, and <c>Update</c>
    /// only overwrites a blob that already exists, skipping the record entirely if it doesn't (checked via one
    /// extra existence call per record).
    /// </summary>
    private static async Task<DestinationWriteResult> WriteIndividualAsync(
        BlobDestinationTarget target,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        PipelineWriteContext context,
        BlobDestinationSettings settings,
        CancellationToken cancellationToken)
    {
        var keyField = ResolveUpsertKeyField(mappingProfile);
        var folder = BuildIndividualFolder(mappingProfile, settings);
        var written = 0;

        foreach (var record in records)
        {
            var keyValue = ResolveKeyValue(record, keyField);

            if (settings.RecordMode == BlobRecordMode.Insert)
            {
                // Never overwrites: the name is always uniquified, even when a stable key resolved, so a
                // repeated "insert" of the same logical record just accumulates another blob rather than
                // replacing the last one.
                var stem = keyValue is not null ? SanitizePathSegment(keyValue) : "record";
                var blobName = $"{folder}/{stem}_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}.json";
                await UploadRecordAsync(target, blobName, mappingProfile, record, context, settings, cancellationToken);
                written++;
                continue;
            }

            if (keyValue is null)
            {
                // Upsert with no resolvable identity still writes (under a one-off unique name — it can't be
                // reliably updated next run without a stable key). Update has nothing to check existence
                // against without a key at all, so it skips rather than guessing.
                if (settings.RecordMode == BlobRecordMode.Update)
                {
                    continue;
                }

                var blobName = $"{folder}/{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}.json";
                await UploadRecordAsync(target, blobName, mappingProfile, record, context, settings, cancellationToken);
                written++;
                continue;
            }

            var keyedBlobName = $"{folder}/{SanitizePathSegment(keyValue)}.json";

            if (settings.RecordMode == BlobRecordMode.Update)
            {
                var blobClient = target.Container.GetBlobClient(keyedBlobName);
                var exists = await blobClient.ExistsAsync(cancellationToken);
                if (!exists.Value)
                {
                    continue;
                }
            }

            await UploadRecordAsync(target, keyedBlobName, mappingProfile, record, context, settings, cancellationToken);
            written++;
        }

        return new DestinationWriteResult(written);
    }

    private static async Task UploadRecordAsync(
        BlobDestinationTarget target,
        string blobName,
        MappingProfile mappingProfile,
        MappedDestinationRecord record,
        PipelineWriteContext context,
        BlobDestinationSettings settings,
        CancellationToken cancellationToken)
    {
        var blobClient = target.Container.GetBlobClient(blobName);

        using var payload = new MemoryStream(Encoding.UTF8.GetBytes(MappedDestinationSerialization.ToJson(record)));
        await blobClient.UploadAsync(
            payload,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
                Metadata = BuildRecordMetadata(mappingProfile, record, context),
                AccessTier = settings.AccessTier is null ? null : (AccessTier?)new AccessTier(settings.AccessTier),
            },
            cancellationToken);
    }

    /// <summary>
    /// The mapped field marked <c>IsUpsertKey</c> for this profile's resource/destination-object scope, if any —
    /// same resolution rule <see cref="MappedMongoDestinationWriter"/>/<see cref="MappedSqlServerDestinationWriter"/>
    /// use, so every destination writer agrees on which field an upsert keys off of.
    /// </summary>
    private static string? ResolveUpsertKeyField(MappingProfile mappingProfile)
    {
        var keyField = mappingProfile.Fields.FirstOrDefault(field =>
            field.IsUpsertKey &&
            field.IsEnabled &&
            (string.IsNullOrWhiteSpace(field.ResourceType) ||
                string.Equals(field.ResourceType, mappingProfile.ResourceType, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(field.DestinationObject) ||
                string.Equals(field.DestinationObject, mappingProfile.DestinationObject, StringComparison.OrdinalIgnoreCase)));

        return keyField?.TargetField;
    }

    private static string? ResolveKeyValue(MappedDestinationRecord record, string? keyField)
    {
        if (keyField is not null && record.Values.TryGetValue(keyField, out var value) && value is not null)
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        return string.IsNullOrWhiteSpace(record.SourceResourceId) ? null : record.SourceResourceId;
    }

    private static string SanitizePathSegment(string value) =>
        string.Join("_", value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

    // Rows saved before this feature existed (or a wizard default left untouched) can carry a CSV-shaped stem
    // like "patients.csv" — stripping it here avoids producing a double-extensioned blob name such as
    // "patients.csv_20260812130455123.ndjson".
    private static readonly string[] KnownStemExtensions = [".ndjson", ".json", ".csv", ".tsv", ".txt"];

    private static string StripKnownExtension(string stem)
    {
        foreach (var extension in KnownStemExtensions)
        {
            if (stem.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return stem[..^extension.Length];
            }
        }

        return stem;
    }

    /// <summary>
    /// Defense in depth: a ";mode=..." write-mode suffix is meant only for MappedSqlServerDestinationWriter's own
    /// parsing (see DestinationNodeExecutors' ApplyWriteModeSuffix, which is explicitly skipped for Blob) — this
    /// guards against one reaching here anyway, the same way MappedMongoDestinationWriter.ValidateCollectionName
    /// strips its own "?..." query suffix rather than trusting the caller never to send one.
    /// </summary>
    private static string StripWriteModeSuffix(string destinationObject)
    {
        var suffixIndex = destinationObject.IndexOf(';', StringComparison.Ordinal);
        return suffixIndex < 0 ? destinationObject : destinationObject[..suffixIndex];
    }

    private static string CleanStem(MappingProfile mappingProfile) =>
        SanitizePathSegment(StripKnownExtension(StripWriteModeSuffix(mappingProfile.DestinationObject)));

    private static string BuildBulkBlobName(MappingProfile mappingProfile, BlobDestinationSettings settings)
    {
        var fileName = $"{CleanStem(mappingProfile)}_{DateTime.UtcNow:yyyyMMddHHmmssfff}.ndjson";

        return string.IsNullOrWhiteSpace(settings.PathPrefix) ? fileName : $"{settings.PathPrefix}/{fileName}";
    }

    private static string BuildIndividualFolder(MappingProfile mappingProfile, BlobDestinationSettings settings)
    {
        var folder = CleanStem(mappingProfile);

        return string.IsNullOrWhiteSpace(settings.PathPrefix) ? folder : $"{settings.PathPrefix}/{folder}";
    }

    private static Dictionary<string, string> BuildBatchMetadata(
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

    private static Dictionary<string, string> BuildRecordMetadata(
        MappingProfile mappingProfile, MappedDestinationRecord record, PipelineWriteContext context)
    {
        return new Dictionary<string, string>
        {
            ["pipelineRunId"] = record.PipelineRunId.ToString(),
            ["resourceType"] = mappingProfile.ResourceType,
            ["destinationObject"] = mappingProfile.DestinationObject,
            ["sourceResourceId"] = record.SourceResourceId ?? string.Empty,
            ["writtenOnUtc"] = DateTime.UtcNow.ToString("o"),
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
