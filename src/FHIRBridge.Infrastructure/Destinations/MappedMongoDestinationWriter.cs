using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
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
/// that field. Also writes <see cref="MappedDestinationRecord.ChildTables"/> — one-to-many data (e.g. a
/// patient's contacts) the mapping canvas routes to a separate collection via a per-field DestinationObject
/// override — into their own collections, mirroring <see cref="MappedSqlServerDestinationWriter"/>'s
/// WriteChildTablesAsync but honoring a per-child-collection upsert key when one is mapped (SQL Server's own
/// child-table writer doesn't; see ResolveUpsertKeyField's per-DestinationObject scoping, already correct on
/// the read side — this was the missing write-side half).
///
/// A <c>ValueType=Json</c> field (the mapping canvas's "whole node as JSON", and any array stored under
/// <see cref="ArrayPolicy.StoreJson"/>) arrives here as raw JSON text. By default it is stored as exactly
/// that — one BSON string — which is what every destination, relational included, has always done. When the
/// mapping selects <see cref="JsonColumnWriteMode.Document"/> instead (see <see cref="MappingFieldFormat"/>,
/// an option offered only for MongoDB destinations), that text is parsed and stored as a real nested BSON
/// sub-document, so the collection can be indexed and queried inside the value with dotted paths.
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

        await EnsureCollectionExistsAsync(database, collectionName, cancellationToken);

        var collection = database.GetCollection<BsonDocument>(collectionName);
        var keyField = ResolveUpsertKeyField(mappingProfile, mappingProfile.DestinationObject);
        var documentJsonColumns = ResolveDocumentJsonColumns(mappingProfile, mappingProfile.DestinationObject);

        // Child collections referenced by this batch — resolved/ensured once, not per record: a child table's
        // name (and therefore its collection and key field) is fixed for the whole mapping profile, so every
        // record needing it reuses the same handle rather than re-listing/re-creating on every single record.
        var childCollections = new Dictionary<string,
            (IMongoCollection<BsonDocument> Collection, string? KeyField, IReadOnlySet<string> DocumentJsonColumns)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var record in records)
        {
            var document = ToBsonDocument(record, documentJsonColumns);

            if (keyField is not null && record.Values.TryGetValue(keyField, out var keyValue) && keyValue is not null)
            {
                var filter = Builders<BsonDocument>.Filter.Eq(keyField, BsonValue.Create(Stringify(keyValue)));
                await collection.ReplaceOneAsync(filter, document, new ReplaceOptions { IsUpsert = true }, cancellationToken);
            }
            else
            {
                await collection.InsertOneAsync(document, cancellationToken: cancellationToken);
            }

            if (record.ChildTables is { Count: > 0 } childTables)
            {
                foreach (var childTable in childTables)
                {
                    await WriteChildTableAsync(
                        database, mappingProfile, record, childTable, childCollections, cancellationToken);
                }
            }
        }

        return new DestinationWriteResult(records.Count);
    }

    /// <summary>
    /// Writes one parent record's rows for a single "child" table/collection — really just a second Mongo
    /// collection some of this resource's fields were routed to (e.g. a patient's contacts), written fully
    /// independently: unlike SQL's relational child tables, a Mongo collection needs no FK linking it back to
    /// a parent to be meaningful on its own, so <see cref="MappedChildTableRecord.ForeignKeyColumn"/> being
    /// blank (the normal case — nothing in the Mongo mapping UI ever sets it; see
    /// ConfiguredPipelineService.BuildChildTableRecords) is not an error. With a mapped upsert key for this
    /// collection (a field whose DestinationObject is it and IsUpsertKey is set — the canvas's per-table key
    /// toggle), each row is upserted individually, keyed on that field, so re-running the pipeline updates
    /// matching documents in place instead of duplicating them. With no key mapped, each row is just inserted —
    /// there's nothing to safely delete-and-replace by without either a key or a parent link, so repeated runs
    /// will accumulate duplicates unless the collection is given its own key.
    /// </summary>
    private static async Task WriteChildTableAsync(
        IMongoDatabase database,
        MappingProfile mappingProfile,
        MappedDestinationRecord record,
        MappedChildTableRecord childTable,
        Dictionary<string,
            (IMongoCollection<BsonDocument> Collection, string? KeyField, IReadOnlySet<string> DocumentJsonColumns)> childCollections,
        CancellationToken cancellationToken)
    {
        if (childTable.Rows.Count == 0)
        {
            return;
        }

        var hasForeignKey = !string.IsNullOrWhiteSpace(childTable.ForeignKeyColumn);
        object? parentKeyValue = null;
        if (hasForeignKey)
        {
            // A parent value missing for this one record just means this record's children go in without
            // the link field rather than aborting the whole write — the collection is still independently
            // valid without it (see the type doc comment).
            record.Values.TryGetValue(childTable.ParentKeyColumn, out parentKeyValue);
        }

        if (!childCollections.TryGetValue(childTable.TableName, out var childInfo))
        {
            var childCollectionName = ValidateCollectionName(childTable.TableName);
            await EnsureCollectionExistsAsync(database, childCollectionName, cancellationToken);
            var childKeyField = ResolveUpsertKeyField(mappingProfile, childTable.TableName);
            childInfo = (
                database.GetCollection<BsonDocument>(childCollectionName),
                childKeyField,
                ResolveDocumentJsonColumns(mappingProfile, childTable.TableName));
            childCollections[childTable.TableName] = childInfo;
        }

        var childDocuments = childTable.Rows
            .Select(row => ToChildBsonDocument(
                row, hasForeignKey ? childTable.ForeignKeyColumn : null, parentKeyValue, childInfo.DocumentJsonColumns))
            .ToList();

        if (childInfo.KeyField is not null)
        {
            foreach (var document in childDocuments)
            {
                if (document.TryGetValue(childInfo.KeyField, out var keyValue) && keyValue != BsonNull.Value)
                {
                    var filter = Builders<BsonDocument>.Filter.Eq(childInfo.KeyField, keyValue);
                    await childInfo.Collection.ReplaceOneAsync(
                        filter, document, new ReplaceOptions { IsUpsert = true }, cancellationToken);
                }
                else
                {
                    // No key value on this particular row — nothing to match an existing document against.
                    await childInfo.Collection.InsertOneAsync(document, cancellationToken: cancellationToken);
                }
            }
        }
        else if (hasForeignKey && parentKeyValue is not null)
        {
            // Only safe to delete-and-replace when there's a real parent link to scope the delete to —
            // otherwise this would wipe the entire collection on every run.
            await childInfo.Collection.DeleteManyAsync(
                Builders<BsonDocument>.Filter.Eq(childTable.ForeignKeyColumn, BsonValue.Create(Stringify(parentKeyValue))),
                cancellationToken);
            await childInfo.Collection.InsertManyAsync(childDocuments, cancellationToken: cancellationToken);
        }
        else
        {
            await childInfo.Collection.InsertManyAsync(childDocuments, cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// Creates the collection when it isn't there yet — for the primary collection and every child collection
    /// alike. Unconditional: the destination form's "Create collection if not exists" opt-in is gone, so a
    /// missing collection is always created rather than failing the run. That restores MongoDB's own native
    /// behaviour (it materializes a collection on first write regardless) and is what makes the mapping
    /// canvas's "type a new collection name" option actually usable — the collection it names exists nowhere
    /// until the first run. The explicit create is kept rather than just letting the insert do it, so the
    /// collection also exists for a run that turns out to write no records.
    ///
    /// Note this drops the old "customer owns the destination, don't create anything they didn't ask for"
    /// stance that the relational writers still take for tables. A Mongo collection is schemaless and free to
    /// create, so there is nothing here to get wrong the way a guessed table shape would be.
    /// </summary>
    private static async Task EnsureCollectionExistsAsync(
        IMongoDatabase database, string collectionName, CancellationToken cancellationToken)
    {
        var existingNames = await (await database.ListCollectionNamesAsync(
            new ListCollectionNamesOptions { Filter = Builders<BsonDocument>.Filter.Eq("name", collectionName) },
            cancellationToken)).ToListAsync(cancellationToken);
        if (existingNames.Count > 0)
        {
            return;
        }

        try
        {
            await database.CreateCollectionAsync(collectionName, cancellationToken: cancellationToken);
        }
        catch (MongoCommandException exception) when (exception.CodeName == "NamespaceExists")
        {
            // Created concurrently by another writer between the existence check above and this call — the
            // collection is there either way, nothing left to do.
        }
    }

    /// <summary>
    /// Builds the document from the record's mapped values. Dates are written as ISO-8601 strings, not BSON's
    /// native date type — <c>Demo_TestApp</c>'s <c>MongoPatientDataSourceReader.GetDateOnly</c> reads this same
    /// shape of document via <c>BsonValue.AsString</c>, which throws on a native BSON date, so every writer into
    /// this collection family must keep dates string-typed for read compatibility.
    ///
    /// <paramref name="documentJsonColumns"/> names the columns whose JSON text is to be stored as a real
    /// nested BSON sub-document instead (see <see cref="ResolveDocumentJsonColumns"/>); every other column,
    /// JSON-valued or not, is written exactly as before.
    /// </summary>
    private static BsonDocument ToBsonDocument(
        MappedDestinationRecord record, IReadOnlySet<string> documentJsonColumns)
    {
        var document = new BsonDocument();
        foreach (var (column, value) in record.Values)
        {
            document[column] = ToBsonValue(value, documentJsonColumns.Contains(column));
        }

        return document;
    }

    /// <summary>
    /// Builds one child-table row's document — same value conversion as ToBsonDocument, plus (only when both
    /// <paramref name="foreignKeyColumn"/> and <paramref name="parentKeyValue"/> are supplied) the parent's key
    /// value stamped under that field name, so the child can optionally be matched back to its parent (Mongo has
    /// no real FK constraint; this is just a plain field, same role MappedSqlServerDestinationWriter's FK column
    /// plays). Neither is required — an independent child collection with no parent link is written just the
    /// same, minus that one field. "RowIndex" is a synthetic key JsonMappingEngine adds internally to align
    /// SeparateDestination rows — not a real mapped field, so it must never reach the document (mirrors
    /// MappedSqlServerDestinationWriter.InsertChildRowsAsync's own exclusion).
    /// </summary>
    private static BsonDocument ToChildBsonDocument(
        IReadOnlyDictionary<string, object?> row,
        string? foreignKeyColumn,
        object? parentKeyValue,
        IReadOnlySet<string> documentJsonColumns)
    {
        var document = new BsonDocument();
        if (!string.IsNullOrWhiteSpace(foreignKeyColumn) && parentKeyValue is not null)
        {
            // The parent's key value copied verbatim — a link field, never a JSON payload of its own.
            document[foreignKeyColumn] = ToBsonValue(parentKeyValue);
        }

        foreach (var (column, value) in row)
        {
            if (string.Equals(column, "RowIndex", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            document[column] = ToBsonValue(value, documentJsonColumns.Contains(column));
        }

        return document;
    }

    /// <summary>
    /// <see cref="ToBsonValue(object?)"/>, plus the one column-scoped exception: when this column's mapping
    /// selected <see cref="JsonColumnWriteMode.Document"/>, its JSON text is parsed into the equivalent BSON
    /// sub-document/array rather than stored as a string. Text that doesn't parse falls back to being stored
    /// as-is — that IS the default behaviour, so a malformed value degrades to the old shape rather than
    /// failing the batch and losing the record.
    /// </summary>
    private static BsonValue ToBsonValue(object? value, bool asDocument)
    {
        if (asDocument
            && value is string json
            && !string.IsNullOrWhiteSpace(json)
            && MongoJsonValueConverter.TryParse(json, out var parsed))
        {
            return parsed;
        }

        return ToBsonValue(value);
    }

    private static BsonValue ToBsonValue(object? value) => value switch
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

    private static string Stringify(object value) => value switch
    {
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>
    /// The mapped field marked <see cref="MappingField.IsUpsertKey"/> for the given <paramref name="destinationObject"/>
    /// (the profile's own primary collection, or one of its child tables) — same resolution rule
    /// <see cref="RelationalDestinationWriterBase"/> and <see cref="MappedSqlServerDestinationWriter"/> use, so all
    /// destination writers agree on which field an upsert keys off of. A blank <see cref="MappingField.DestinationObject"/>
    /// only ever means "this profile's own primary collection" (every child field carries an explicit, non-blank
    /// override — see workflow-build-assembler.service.ts's isChildTableField), so that fallback is deliberately
    /// scoped to <paramref name="destinationObject"/> being the primary one; a child table's key must match exactly.
    /// </summary>
    private static string? ResolveUpsertKeyField(MappingProfile mappingProfile, string destinationObject)
    {
        var isPrimaryObject = string.Equals(
            destinationObject, mappingProfile.DestinationObject, StringComparison.OrdinalIgnoreCase);

        var keyField = mappingProfile.Fields.FirstOrDefault(field =>
            field.IsUpsertKey &&
            field.IsEnabled &&
            (string.IsNullOrWhiteSpace(field.ResourceType) ||
                string.Equals(field.ResourceType, mappingProfile.ResourceType, StringComparison.OrdinalIgnoreCase)) &&
            (string.Equals(field.DestinationObject, destinationObject, StringComparison.OrdinalIgnoreCase) ||
                (isPrimaryObject && string.IsNullOrWhiteSpace(field.DestinationObject))));

        return keyField?.TargetField;
    }

    /// <summary>
    /// The columns of <paramref name="destinationObject"/> (this profile's own primary collection, or one of
    /// its child collections) whose mapping asked for its JSON to be stored as a native BSON sub-document
    /// rather than as text. Scoped to the destination object exactly the way
    /// <see cref="ResolveUpsertKeyField"/> is — a blank <see cref="MappingField.DestinationObject"/> only
    /// ever means the profile's own primary collection — so a column of the same name on two collections
    /// can make the choice independently.
    ///
    /// Only <see cref="MappingValueType.Json"/> fields qualify: those are the ones whose value is raw JSON
    /// text by construction (see JsonMappingEngine). A String-typed field that merely happens to contain
    /// something JSON-shaped is left alone, since "parse my text and restructure it" is not what its mapping
    /// asked for.
    /// </summary>
    private static IReadOnlySet<string> ResolveDocumentJsonColumns(
        MappingProfile mappingProfile, string destinationObject)
    {
        var isPrimaryObject = string.Equals(
            destinationObject, mappingProfile.DestinationObject, StringComparison.OrdinalIgnoreCase);

        return mappingProfile.Fields
            .Where(field =>
                field.IsEnabled &&
                field.ValueType == MappingValueType.Json &&
                MappingFieldFormat.ReadJsonWriteMode(field.Format) == JsonColumnWriteMode.Document &&
                (string.IsNullOrWhiteSpace(field.ResourceType) ||
                    string.Equals(field.ResourceType, mappingProfile.ResourceType, StringComparison.OrdinalIgnoreCase)) &&
                (string.Equals(field.DestinationObject, destinationObject, StringComparison.OrdinalIgnoreCase) ||
                    (isPrimaryObject && string.IsNullOrWhiteSpace(field.DestinationObject))))
            .Select(field => field.TargetField)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string ValidateCollectionName(string destinationObject)
    {
        var name = MongoCollectionNameResolver.Resolve(destinationObject);

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Destination object must name a collection.");
        }

        return name;
    }
}
