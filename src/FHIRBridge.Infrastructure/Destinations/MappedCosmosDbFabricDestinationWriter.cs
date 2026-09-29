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

    /// <summary>
    /// The smallest autoscale maximum Cosmos DB accepts. An autoscale container scales between 10% and 100% of
    /// this, so 1000 idles at 100 RU/s — the cheapest a created container can be while still satisfying
    /// Fabric's autoscale-only rule. Whoever owns the data raises it in Fabric if the workload needs more;
    /// FHIRBridge does not guess a capacity for a container it is creating on someone else's behalf.
    /// </summary>
    private const int MinimumAutoscaleMaxRuPerSecond = 1000;

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
            () => GetContainerAsync(client, settings, containerName, destination, _logger, cancellationToken),
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
        ILogger logger,
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
            if (!settings.CanCreateContainer)
            {
                throw new InvalidOperationException(
                    $"Destination '{destination.Name}': container '{containerName}' does not exist in Cosmos DB "
                        + $"database '{settings.Database}'. Create it in Fabric before running this pipeline, or "
                        + "set this destination to create missing containers — FHIRBridge does not create them by "
                        + "default, because a container's partition key is chosen at creation and cannot be "
                        + "changed afterwards.",
                    exception);
            }

            return await CreateContainerAsync(
                client, settings, containerName, destination, logger, cancellationToken);
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
    /// Creates a missing container, for a destination that opted into it.
    ///
    /// <para>The partition key is whatever <see cref="CosmosDbFabricDestinationSettings.PartitionKeyPathForNewContainer"/>
    /// resolves — the user's configured path, or <c>/id</c> under the mode that explicitly accepts a default.
    /// Every create is logged with the key it used, because the choice cannot be revisited: a container built on
    /// the wrong key is replaced, not altered, and the run log is where someone investigating that later will
    /// look. A create on a key the user did not choose is logged as a WARNING rather than information, since
    /// that is the case most likely to be regretted.</para>
    /// </summary>
    private static async Task<Container> CreateContainerAsync(
        CosmosClient client,
        CosmosDbFabricDestinationSettings settings,
        string containerName,
        DestinationConfiguration destination,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var partitionKeyPath = settings.PartitionKeyPathForNewContainer!;

        if (settings.CreatesOnUnchosenPartitionKey)
        {
            logger.LogWarning(
                "Creating Cosmos DB container {Database}/{Container} for destination {DestinationId} with the "
                    + "DEFAULT partition key {PartitionKeyPath}, which nobody chose for this data. A partition "
                    + "key cannot be changed after creation: if this container is later queried by anything "
                    + "other than id, or needs cross-document transactions, it has to be rebuilt and reloaded. "
                    + "Set a partition key path on the destination to choose deliberately.",
                settings.Database,
                containerName,
                destination.Id,
                partitionKeyPath);
        }
        else
        {
            logger.LogInformation(
                "Creating Cosmos DB container {Database}/{Container} for destination {DestinationId} with "
                    + "partition key {PartitionKeyPath}.",
                settings.Database,
                containerName,
                destination.Id,
                partitionKeyPath);
        }

        try
        {
            // CreateContainerIfNotExistsAsync rather than CreateContainerAsync: two resource types mapped to
            // the same container can reach here concurrently, and losing that race must not fail the run.
            //
            // Autoscale is REQUIRED, not a preference. Cosmos DB in Fabric restricts accounts to autoscale
            // offers, and omitting throughput entirely asks for a MANUAL one — which the service rejects with
            // "Offer Type is restricted to Autoscale for your account", an error naming neither the container
            // nor the setting that caused it. The minimum autoscale maximum (1000 RU/s, which idles down to
            // 100) is used deliberately: this is a container FHIRBridge is creating on the customer's behalf,
            // so it takes the smallest offer the service allows and leaves scaling up to whoever owns the
            // bill. Autoscale means that costs nothing extra while the container is idle.
            var database = client.GetDatabase(settings.Database);
            var response = await database.CreateContainerIfNotExistsAsync(
                new ContainerProperties(containerName, partitionKeyPath),
                ThroughputProperties.CreateAutoscaleThroughput(MinimumAutoscaleMaxRuPerSecond),
                cancellationToken: cancellationToken);

            return response.Container;
        }
        catch (CosmosException exception) when (
            exception.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            // Creating needs a strictly stronger role than writing, so this fails for identities that could
            // otherwise run the pipeline perfectly well against an existing container.
            throw new InvalidOperationException(
                $"Destination '{destination.Name}': the configured identity reached Cosmos DB but was not "
                    + $"allowed to create container '{containerName}' in database '{settings.Database}'. "
                    + "Creating a container needs a higher data-plane role than writing to one — either grant "
                    + "it in Fabric, or create the container there and set this destination back to not "
                    + "creating them.",
                exception);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.BadRequest)
        {
            // A 400 here is about the OFFER or the container definition, not about connectivity, and the SDK
            // reports it as a wall of request-routing detail with the real sentence buried inside. Restated
            // with the container named, since that is what the reader has to act on.
            throw new InvalidOperationException(
                $"Destination '{destination.Name}': Cosmos DB refused to create container '{containerName}' in "
                    + $"database '{settings.Database}'. This is usually the throughput offer or the partition "
                    + $"key path ('{partitionKeyPath}') rather than a connection problem. The underlying "
                    + $"message was: {exception.ResponseBody}",
                exception);
        }
    }

    /// <summary>
    /// Builds one document, with the <c>id</c> Cosmos requires.
    ///
    /// <para>Values are written as JSON strings, matching what the mapping pipeline actually produces (see
    /// <see cref="MappedDestinationSerialization.GetCell"/>): every cell arrives already stringly-typed, so
    /// emitting numbers or booleans here would mean guessing at a type the pipeline did not assert.</para>
    ///
    /// <para><b>A plain Dictionary, deliberately — not a <c>JsonObject</c>.</b> The Cosmos SDK serializes items
    /// with <b>Newtonsoft.Json</b>, which does not understand <c>System.Text.Json</c>'s node types: handed a
    /// <see cref="JsonObject"/> it serializes it as an <c>IDictionary</c> and walks each child's <c>Parent</c>
    /// back to the root, failing with "Self referencing loop detected for property 'Parent'". That is a
    /// serializer mismatch rather than anything wrong with the document, and it surfaces only at write time
    /// against a real container — so the document is built out of types both serializers agree on.</para>
    /// </summary>
    internal static Dictionary<string, object?> ToJsonDocument(
        MappedDestinationRecord record, string? keyField)
    {
        var document = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (column, value) in record.Values)
        {
            // "id" is Cosmos's own document identifier. A mapping that targets it wins over the derived id
            // below — the customer has said explicitly what identifies the document.
            document[column] = value is null ? null : Stringify(value);
        }

        if (!document.TryGetValue("id", out var existingId) || existingId is null)
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

    /// <summary>
    /// A cell as the string this writer stores.
    ///
    /// <para>A <see cref="JsonNode"/> is serialized to its JSON text rather than left as a node. Most cells
    /// arrive as raw JSON TEXT already (see <c>ArrayPolicy.StoreJson</c>), but a field routed through a
    /// Transformation node arrives as a LIVE node instead — the two are indistinguishable downstream, and
    /// letting the live one through is what produced Newtonsoft's self-referencing-loop failure. Converting
    /// here means both shapes land in Cosmos as the same JSON text.</para>
    /// </summary>
    private static string Stringify(object value) => value switch
    {
        string text => text,
        // ToJsonString(), not ToString(): for a JsonObject the two agree, but for a JsonValue holding a string
        // ToString() returns the bare value while ToJsonString() returns it quoted. The bare form is what the
        // rest of the pipeline stores for a scalar, so the node's own text is taken for objects and arrays and
        // the scalar path is left to GetValue below.
        JsonValue scalar => scalar.TryGetValue<string>(out var text) ? text : scalar.ToJsonString(),
        JsonNode node => node.ToJsonString(),
        _ => value.ToString() ?? string.Empty,
    };
}
