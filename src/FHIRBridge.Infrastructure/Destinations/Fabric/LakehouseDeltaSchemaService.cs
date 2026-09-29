using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Destinations.Fabric;

/// <summary>
/// Serves schema reads for a Lakehouse Delta destination and delegates everything else to the SQL
/// implementation it wraps.
///
/// <para><b>Why a decorator rather than a branch inside the SQL service.</b> That service is relational
/// throughout — every method opens a <c>SqlConnection</c> — and a Delta "table" is a folder in blob storage with
/// a transaction log. The two share a contract and nothing else, so intercepting the one type here keeps the SQL
/// path exactly as it was: a destination this does not handle is passed straight through, unexamined.</para>
///
/// <para><b>Read-only, deliberately.</b> The four DDL mutations are delegated unchanged, which means they refuse
/// a Delta destination the same way they refuse any non-relational one. Creating or altering a Delta table from
/// here would mean writing a commit that changes the table's schema — a real write to the customer's table,
/// issued from a screen the user opened to look at it. The writer creates a table on its first write, which is
/// where that belongs; see <see cref="LakehouseTableLandingStrategy"/>.</para>
/// </summary>
internal sealed class LakehouseDeltaSchemaService : IDestinationSchemaService
{
    private readonly IDestinationSchemaService _inner;
    private readonly IOneLakeClientFactory _clientFactory;
    private readonly IConfigurationRepository _configurationRepository;
    private readonly ILogger<LakehouseDeltaSchemaService> _logger;

    public LakehouseDeltaSchemaService(
        IDestinationSchemaService inner,
        IOneLakeClientFactory clientFactory,
        IConfigurationRepository configurationRepository,
        ILogger<LakehouseDeltaSchemaService> logger)
    {
        _inner = inner;
        _clientFactory = clientFactory;
        _configurationRepository = configurationRepository;
        _logger = logger;
    }

    /// <summary>
    /// Reads a SAVED destination's tables. Only a Fabric destination configured for the Delta landing mode is
    /// handled here — a OneLake Files destination has no tables at all, and falls through to the inner service,
    /// which returns an empty list for a non-relational type exactly as before.
    /// </summary>
    public async Task<DestinationSchemaDto> GetSchemaAsync(Guid destinationId, CancellationToken cancellationToken)
    {
        var destination = await _configurationRepository.GetDestinationAsync(destinationId, cancellationToken);
        if (destination is null || !IsLakehouseDelta(destination))
        {
            return await _inner.GetSchemaAsync(destinationId, cancellationToken);
        }

        try
        {
            var settings = FabricDestinationSettings.Parse(destination);
            var workspace = await _clientFactory.GetWorkspaceAsync(destination, settings, cancellationToken);
            var tables = await LakehouseDeltaSchemaReader.ListTablesAsync(
                workspace.Container, settings, cancellationToken);

            return new DestinationSchemaDto(destinationId, tables);
        }
        catch (Exception exception)
        {
            // GetSchemaAsync has no error channel — it returns tables or none. An unreachable lakehouse must
            // therefore degrade to "no tables to offer" rather than failing the screen, with the reason in the
            // log. ProbeSchemaAsync below is the path that reports the error to the user.
            _logger.LogWarning(
                exception,
                "Could not list Delta tables for destination {DestinationId}; the mapping canvas will offer none.",
                destinationId);

            return new DestinationSchemaDto(destinationId, []);
        }
    }

    /// <summary>
    /// Probes an ad-hoc (unsaved) Delta destination — the wizard's "test connection, then pick a table" flow.
    /// Reaching OneLake at all is the connection test; the tables it finds are the result.
    /// </summary>
    public async Task<DestinationSchemaProbeDto> ProbeSchemaAsync(
        DestinationConnectionProbeRequest request,
        CancellationToken cancellationToken)
    {
        if (request.DestinationType != DestinationType.DataFabricAzure
            || !string.Equals(request.FabricLandingMode, "lakehouseTable", StringComparison.OrdinalIgnoreCase))
        {
            return await _inner.ProbeSchemaAsync(request, cancellationToken);
        }

        try
        {
            var settings = FabricDestinationSettings.Parse(BuildAdHocDestination(request));

            // Ad-hoc, not the saved-destination path: there is no Key Vault reference to resolve yet, so the
            // secret the user just typed is passed straight through.
            var workspace = await _clientFactory.GetWorkspaceAdHocAsync(
                settings, request.FabricSecret, cancellationToken);
            var tables = await LakehouseDeltaSchemaReader.ListTablesAsync(
                workspace.Container, settings, cancellationToken);

            // Connected with no tables is a legitimate, common answer: a lakehouse whose Tables/ area is empty
            // is exactly where a first Delta write lands. The canvas shows "type a new table name" for it.
            return new DestinationSchemaProbeDto(true, null, tables);
        }
        catch (Exception exception)
        {
            // Connection/auth failures are an expected UI outcome, not a server error — same contract the SQL
            // probe follows.
            return new DestinationSchemaProbeDto(false, exception.Message, []);
        }
    }

    // The four DDL mutations pass straight through. See the class remarks: schema changes to a Delta table are
    // commits, and they belong to the writer rather than to a screen the user opened to look at the table.
    public Task<SchemaMutationResultDto> AddColumnAsync(
        AddColumnRequest request, CancellationToken cancellationToken)
        => _inner.AddColumnAsync(request, cancellationToken);

    public Task<SchemaMutationResultDto> CreateTableAsync(
        CreateTableRequest request, CancellationToken cancellationToken)
        => _inner.CreateTableAsync(request, cancellationToken);

    public Task<SchemaMutationResultDto> DropColumnAsync(
        DropColumnRequest request, CancellationToken cancellationToken)
        => _inner.DropColumnAsync(request, cancellationToken);

    public Task<SchemaMutationResultDto> AlterColumnAsync(
        AlterColumnRequest request, CancellationToken cancellationToken)
        => _inner.AlterColumnAsync(request, cancellationToken);

    private static bool IsLakehouseDelta(DestinationConfiguration destination)
    {
        if (destination.DestinationType != DestinationType.DataFabricAzure)
        {
            return false;
        }

        // Parse rather than a raw metadata read, so this agrees with the writer about what the mode is — a
        // blank or absent mode means OneLake Files, which has no tables.
        try
        {
            return FabricDestinationSettings.Parse(destination).Mode == FabricLandingMode.LakehouseTable;
        }
        catch (Exception)
        {
            // An unparseable destination is not a Delta one for this purpose; the inner service's own handling
            // of it is unchanged.
            return false;
        }
    }

    /// <summary>
    /// Rebuilds the connection-metadata shape <see cref="FabricDestinationSettings.Parse"/> reads, from the
    /// discrete probe fields. Keeping one parser means an ad-hoc probe and a saved destination resolve their
    /// workspace, item and credentials identically — the probe cannot succeed against an address the writer
    /// would then fail to reach.
    /// </summary>
    private static DestinationConfiguration BuildAdHocDestination(DestinationConnectionProbeRequest request)
    {
        var metadata = new Dictionary<string, string?>
        {
            ["dest_fabricMode"] = "lakehouseTable",
            ["dest_fabricWorkspace"] = request.FabricWorkspace,
            ["dest_fabricItemName"] = request.FabricItemName,
            ["dest_fabricItemType"] = "Lakehouse",
            ["dest_fabricAuthMode"] = request.FabricAuthMode,
            ["dest_fabricTenantId"] = request.FabricTenantId,
            ["dest_fabricClientId"] = request.FabricClientId,
            ["dest_fabricManagedIdentityClientId"] = request.FabricManagedIdentityClientId,
            ["dest_fabricEndpointSuffix"] = request.FabricEndpointSuffix,
            ["dest_fabricAuthorityHost"] = request.FabricAuthorityHost,
            ["dest_fabricLakehouseSchema"] = request.FabricLakehouseSchema,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(
            metadata.Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value!));

        // A placeholder secret reference: this destination exists only to be handed to
        // FabricDestinationSettings.Parse, and the ad-hoc client path never dereferences it — the typed secret
        // is passed to GetWorkspaceAdHocAsync directly.
        return new DestinationConfiguration(
            "Lakehouse probe",
            DestinationType.DataFabricAzure,
            new SecretReference("unsaved", "unsaved"),
            null,
            json);
    }
}
