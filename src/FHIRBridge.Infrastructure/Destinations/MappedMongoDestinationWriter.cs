using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.ValueObjects;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Writes mapped records to a MongoDB collection (customer-owned destination; Insert and Upsert write modes,
/// mirroring <see cref="MappedMySqlDestinationWriter"/>'s conventions). The connection string's own database
/// segment selects the database; <see cref="MappingProfile.DestinationObject"/> is the bare collection name
/// (no schema concept, same as MySQL). Upsert requires one mapped field flagged
/// <see cref="MappingField.IsUpsertKey"/>, applied via <c>ReplaceOneAsync(..., IsUpsert = true)</c> keyed on
/// that field.
/// </summary>
public sealed class MappedMongoDestinationWriter : IConfiguredDestinationWriter
{
    private readonly ISecretProvider _secretProvider;

    public MappedMongoDestinationWriter(ISecretProvider secretProvider)
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

        var connectionString = await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken);
        var collectionName = ValidateCollectionName(destination.Target ?? mappingProfile.DestinationObject);

        var client = new MongoClient(connectionString);
        var database = client.GetDatabase(new MongoUrl(connectionString).DatabaseName
            ?? throw new InvalidOperationException("The Mongo connection string must include a database name."));

        // The customer owns the destination — same "does not exist, create it first" contract every other
        // writer enforces, even though MongoDB itself would otherwise auto-create the collection on first write.
        var existingNames = await (await database.ListCollectionNamesAsync(
            new ListCollectionNamesOptions { Filter = Builders<BsonDocument>.Filter.Eq("name", collectionName) },
            cancellationToken)).ToListAsync(cancellationToken);
        if (existingNames.Count == 0)
        {
            throw new InvalidOperationException(
                $"Destination collection '{collectionName}' does not exist. Create it in your database before running this pipeline.");
        }

        var collection = database.GetCollection<BsonDocument>(collectionName);
        var keyField = ResolveUpsertKeyField(mappingProfile);

        foreach (var record in records)
        {
            var document = ToBsonDocument(record);

            if (keyField is not null && record.Values.TryGetValue(keyField, out var keyValue) && keyValue is not null)
            {
                var filter = Builders<BsonDocument>.Filter.Eq(keyField, BsonValue.Create(Stringify(keyValue)));
                await collection.ReplaceOneAsync(filter, document, new ReplaceOptions { IsUpsert = true }, cancellationToken);
            }
            else
            {
                await collection.InsertOneAsync(document, cancellationToken: cancellationToken);
            }
        }

        return new DestinationWriteResult(records.Count);
    }

    /// <summary>
    /// Builds the document from the record's mapped values. Dates are written as ISO-8601 strings, not BSON's
    /// native date type — <c>Demo_TestApp</c>'s <c>MongoPatientDataSourceReader.GetDateOnly</c> reads this same
    /// shape of document via <c>BsonValue.AsString</c>, which throws on a native BSON date, so every writer into
    /// this collection family must keep dates string-typed for read compatibility.
    /// </summary>
    private static BsonDocument ToBsonDocument(MappedDestinationRecord record)
    {
        var document = new BsonDocument();
        foreach (var (column, value) in record.Values)
        {
            document[column] = value switch
            {
                null => BsonNull.Value,
                DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                // BsonValue.Create has no built-in mapping for Guid (BSON's binary-subtype representation for
                // GUIDs is driver/language-specific and this MongoDB.Driver version refuses to guess one) — write
                // it as a plain string, consistent with every other "needs an explicit string form" type above.
                Guid guid => guid.ToString(),
                _ => BsonValue.Create(value),
            };
        }

        return document;
    }

    private static string Stringify(object value) => value switch
    {
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>
    /// The mapped field marked <see cref="MappingField.IsUpsertKey"/> for this profile's resource/destination-object
    /// scope, if any — same resolution rule <see cref="RelationalDestinationWriterBase"/> and
    /// <see cref="MappedSqlServerDestinationWriter"/> use, so all destination writers agree on which field an
    /// upsert keys off of.
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

    private static string ValidateCollectionName(string destinationObject)
    {
        var name = destinationObject.Trim();
        var queryIndex = name.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            name = name[..queryIndex];
        }

        // Mongo has no schema layer distinct from the database (same convention as MySQL) — a "Schema.Collection"
        // style destination object only ever needs the last segment.
        var dotIndex = name.LastIndexOf('.');
        if (dotIndex >= 0)
        {
            name = name[(dotIndex + 1)..];
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Destination object must name a collection.");
        }

        return name;
    }
}
