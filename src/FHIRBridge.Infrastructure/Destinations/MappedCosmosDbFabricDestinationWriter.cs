using System.Net;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations.Fabric;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Writes mapped records as documents to a container in Cosmos DB in Microsoft Fabric.
///
/// <para><b>Why this exists rather than reusing the Mongo writer.</b> Cosmos DB in Fabric is the NoSQL API —
/// the same engine as Azure Cosmos DB for NoSQL — and does not speak the MongoDB wire protocol, so
/// <c>MongoDB.Driver</c> cannot reach it. See <c>DestinationType.CosmosDbFabric</c>.</para>
///
/// <para><b>The customer owns the container.</b> A missing database or container is an error naming it, never a
/// create: <c>docs/backend/11-destination-schema-ownership-plan.md</c> reserves destination schema to the
/// customer, and a container's partition key is a permanent choice this writer has no business making on their
/// behalf — the wrong one cannot be corrected later without rebuilding the container.</para>
///
/// <para><b>Upsert by default.</b> Cosmos identifies a document by <c>id</c> within a partition, so a write must
/// supply one. The mapping's upsert key (or SourceResourceId, the same convention every other writer follows)
/// becomes that id, which makes re-running a pipeline replace documents in place. With neither mapped, a fresh
/// guid is used per record and re-runs accumulate copies — stated in the log rather than failed, since an
/// append-only feed is a legitimate configuration.</para>
///
/// <para><b>Unverified against a live Fabric tenant.</b> The shape below follows Microsoft's documented client
/// construction, including the Gateway connection mode Fabric requires. If it fails on first contact, the two
/// things most likely to need attention are the tenant setting "Service principals can use Fabric APIs" and the
/// identity's data-plane role on the database — neither of which is visible in the resulting error.</para>
/// </summary>
public sealed class MappedCosmosDbFabricDestinationWriter : IConfiguredDestinationWriter
{
    private readonly ICosmosDbFabricClientFactory _clientFactory;
    private readonly ILogger<MappedCosmosDbFabricDestinationWriter> _logger;

    public MappedCosmosDbFabricDestinationWriter(
        ICosmosDbFabricClientFactory clientFactory,
        ILogger<MappedCosmosDbFabricDestinationWriter> logger)
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

        var settings = CosmosDbFabricDestinationSettings.Parse(destination);
        var containerName = settings.ResolveContainer(mappingProfile);

        var client = await _clientFactory.GetClientAsync(destination, settings, cancellationToken);

        // Reported around the first real round trip, not around the client constructor: CosmosClient connects
        // lazily, so constructing one against an unreachable endpoint or an unusable identity succeeds silently.
        // Reporting "Connected" there would claim a connection that has not happened yet — the same reasoning
        // MappedMongoDestinationWriter documents for its own lazily-connecting driver.
        var container = await context.ReportConnectAsync(
            () => GetContainerAsync(client, settings, containerName, destination, cancellationToken),
            cancellationToken,
            detail: "Cosmos DB (Fabric)");

        var keyField = ResolveUpsertKeyField(mappingProfile);
        var written = 0;

        foreach (var record in records)
        {
            var document = ToJsonDocument(record, keyField);

            // UpsertItemAsync, not CreateItemAsync: re-running a pipeline over the same source data should
            // converge on one document per resource rather than fail on the second run with a conflict.
            await container.UpsertItemAsync(document, cancellationToken: cancellationToken);
            written++;
        }

        _logger.LogInformation(
            "Wrote {RecordCount} {ResourceType} document(s) to Cosmos DB (Fabric) {Database}/{Container} for "
                + "destination {DestinationId}{KeyNote}.",
            written,
            mappingProfile.ResourceType,
            settings.Database,
            containerName,
            destination.Id,
            keyField is null
                ? " with generated ids (no upsert key mapped, so re-runs add new documents)"
                : $" keyed on {keyField}");

        return new DestinationWriteResult(written);
    }

    /// <summary>
    /// Resolves the target container, failing with an actionable message when the database or container does not
    /// exist.
    ///
    /// <para>Deliberately a read, not a <c>CreateIfNotExists</c>. Beyond the customer-owned-schema rule, a
    /// container is created with a partition key that can never be changed afterwards — picking one here would
    /// silently commit the customer to a layout chosen by a default, and the cost of the wrong choice is
    /// rebuilding the container and re-loading its data.</para>
    /// </summary>
    private static async Task<Container> GetContainerAsync(
        CosmosClient client,
        CosmosDbFabricDestinationSettings settings,
        string containerName,
        DestinationConfiguration destination,
        CancellationToken cancellationToken)
    {
        var container = client.GetContainer(settings.Database, containerName);

        try
        {
            // The first real round trip: resolves the container and, in doing so, proves the endpoint,
            // the credential and the identity's data-plane role all work.
            await container.ReadContainerAsync(cancellationToken: cancellationToken);

            return container;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"Destination '{destination.Name}': container '{containerName}' does not exist in Cosmos DB "
                    + $"database '{settings.Database}'. Create it in Fabric before running this pipeline — "
                    + "FHIRBridge does not create containers, because a container's partition key is chosen at "
                    + "creation and cannot be changed afterwards.",
                exception);
        }
        catch (CosmosException exception) when (
            exception.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException(
                $"Destination '{destination.Name}': the configured identity reached Cosmos DB but was refused "
                    + $"access to database '{settings.Database}'. Cosmos DB in Fabric authorizes through "
                    + "built-in data-plane roles, so the identity needs at least Read on the database item in "
                    + "Fabric. For a service principal, also confirm the tenant setting 'Service principals can "
                    + "use Fabric APIs' is enabled — without it a correctly-permissioned principal is still "
                    + "refused, and nothing in this error says so.",
                exception);
        }
    }

    /// <summary>
    /// Builds one document, with the <c>id</c> Cosmos requires.
    ///
    /// <para>Values are written as JSON strings, matching what the mapping pipeline actually produces (see
    /// <see cref="MappedDestinationSerialization.GetCell"/>): every cell arrives already stringly-typed, so
    /// emitting numbers or booleans here would mean guessing at a type the pipeline did not assert.</para>
    /// </summary>
    internal static JsonObject ToJsonDocument(MappedDestinationRecord record, string? keyField)
    {
        var document = new JsonObject();

        foreach (var (column, value) in record.Values)
        {
            // "id" is Cosmos's own document identifier. A mapping that targets it wins over the derived id
            // below — the customer has said explicitly what identifies the document.
            document[column] = value is null ? null : JsonValue.Create(Stringify(value));
        }

        if (document["id"] is null)
        {
            var keyValue = keyField is not null
                && record.Values.TryGetValue(keyField, out var mapped)
                && mapped is not null
                    ? Stringify(mapped)
                    : null;

            // A guid when nothing identifies the record: Cosmos rejects a document with no id outright, so the
            // alternative to generating one is failing the write. An append-only feed is a legitimate setup,
            // and the log line at the call site says which of the two happened.
            document["id"] = keyValue is { Length: > 0 } ? keyValue : Guid.NewGuid().ToString();
        }

        return document;
    }

    /// <summary>
    /// The mapping field flagged as the upsert key, else <c>SourceResourceId</c> — the same convention the SQL
    /// Server, Mongo and Fabric Warehouse writers use, so a key configured once means the same thing everywhere.
    /// </summary>
    internal static string? ResolveUpsertKeyField(MappingProfile mappingProfile)
    {
        // Scoped exactly as the other writers scope it: enabled fields only, and only those whose resource type
        // and destination object match this profile (blank meaning "any"). A profile can carry fields for
        // several scopes, so an unscoped search would pick up another container's key.
        var configured = mappingProfile.Fields.FirstOrDefault(field =>
            field.IsUpsertKey
            && field.IsEnabled
            && (string.IsNullOrWhiteSpace(field.ResourceType)
                || string.Equals(field.ResourceType, mappingProfile.ResourceType, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(field.DestinationObject)
                || string.Equals(
                    field.DestinationObject, mappingProfile.DestinationObject, StringComparison.OrdinalIgnoreCase)))
            ?.TargetField;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return mappingProfile.Fields.Any(field =>
            string.Equals(field.TargetField, "SourceResourceId", StringComparison.OrdinalIgnoreCase))
            ? "SourceResourceId"
            : null;
    }

    private static string Stringify(object value)
        => value as string ?? value.ToString() ?? string.Empty;
}
