using System.Text.Json;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Infrastructure.Workflows;

public sealed class RouteToWorkflowGraphProjection : ILaunchWorkflowProjection
{
    // Web defaults match the node executors' JsonOptions so the config they read back deserializes cleanly.
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web);

    // Canonical ranks from DefaultWorkflowNodeCatalog. A projected chain must use these exact ranks/categories
    // or WorkflowGraphValidator rejects it.
    private const int SourceRank = 0;
    private const int DeIdentificationRank = 50;
    private const int MappingRank = 60;
    private const int DestinationRank = 70;

    private readonly IConfigurationRepository _configurationRepository;

    public RouteToWorkflowGraphProjection(IConfigurationRepository configurationRepository)
    {
        _configurationRepository = configurationRepository;
    }

    public async Task<WorkflowDefinition?> ProjectForSourceAsync(
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        var source = await _configurationRepository.GetSourceConnectionAsync(sourceConnectionId, cancellationToken);
        if (source is null || !source.IsEnabled)
        {
            return null;
        }

        var routes = await _configurationRepository.GetRoutesAsync(cancellationToken);
        var mappings = (await _configurationRepository.GetMappingProfilesAsync(cancellationToken))
            .ToDictionary(mapping => mapping.Id);
        var destinations = (await _configurationRepository.GetDestinationsAsync(cancellationToken))
            .ToDictionary(destination => destination.Id);

        var chains = ResolveChains(sourceConnectionId, routes, mappings, destinations).ToList();
        if (chains.Count == 0)
        {
            return null;
        }

        // Named deterministically from the source id so the resolver maps a source to a single durable launch graph.
        var workflow = new WorkflowDefinition(Guid.NewGuid(), LaunchWorkflowNaming.ForSource(sourceConnectionId), version: 1);

        for (var chainIndex = 0; chainIndex < chains.Count; chainIndex++)
        {
            AddChain(workflow, source, chains[chainIndex], chainIndex);
        }

        return workflow;
    }

    // One projected chain per enabled (route × mapping) whose mapping binds to this source and lands on a supported
    // destination node. Search parameters follow the same per-mapping-then-route precedence as the route pipeline.
    private static IEnumerable<ProjectedChain> ResolveChains(
        Guid sourceConnectionId,
        IReadOnlyList<ResourcePipelineRoute> routes,
        IReadOnlyDictionary<Guid, MappingProfile> mappings,
        IReadOnlyDictionary<Guid, DestinationConfiguration> destinations)
    {
        foreach (var route in routes.Where(route => route.IsEnabled))
        {
            var routeMappings = route.ResourceMappings.Count == 0
                ? new[] { (MappingProfileId: route.MappingProfileId, route.SearchParameters, IsEnabled: true) }
                : route.ResourceMappings
                    .OrderBy(mapping => mapping.ExecutionOrder)
                    .ThenBy(mapping => mapping.MappingProfileId)
                    .Select(mapping => (mapping.MappingProfileId, mapping.SearchParameters ?? route.SearchParameters, mapping.IsEnabled))
                    .ToArray();

            foreach (var (mappingProfileId, searchParameters, isMappingEnabled) in routeMappings)
            {
                if (!isMappingEnabled ||
                    !mappings.TryGetValue(mappingProfileId, out var mapping) ||
                    !mapping.IsEnabled ||
                    mapping.SourceConnectionId != sourceConnectionId ||
                    !destinations.TryGetValue(mapping.DestinationId, out var destination) ||
                    !destination.IsEnabled)
                {
                    continue;
                }

                if (MapDestinationNodeType(destination.DestinationType) is not { } destinationNodeType)
                {
                    // Destination type has no exposed catalog node yet — leave it on the route path.
                    continue;
                }

                yield return new ProjectedChain(mapping, destination, destinationNodeType, searchParameters);
            }
        }
    }

    private static void AddChain(
        WorkflowDefinition workflow,
        SourceConnection source,
        ProjectedChain chain,
        int chainIndex)
    {
        var mapping = chain.Mapping;
        var laneY = chainIndex * 160;

        var sourceConfiguration = BuildSecretFreeSourceConfiguration(source, chain.SearchParameters);
        var sourceNode = workflow.AddNode(
            MapSourceNodeType(source.SourceSystemType),
            WorkflowNodeCategory.Source,
            SourceRank,
            subRank: chainIndex,
            displayName: $"{source.Name} ({mapping.ResourceType})",
            configurationJson: Serialize(new Dictionary<string, object?>
            {
                ["source"] = sourceConfiguration,
                ["resourceType"] = mapping.ResourceType
            }),
            positionX: 0,
            positionY: laneY);

        var deIdentificationNode = workflow.AddNode(
            WorkflowNodeTypes.DeIdentification,
            WorkflowNodeCategory.Compliance,
            DeIdentificationRank,
            subRank: chainIndex,
            displayName: $"De-identify ({mapping.ResourceType})",
            // deIdentify:false mirrors the route path, which de-identifies only when governance requires it.
            configurationJson: Serialize(new Dictionary<string, object?> { ["deIdentify"] = false }),
            positionX: 250,
            positionY: laneY);

        var mappingFields = mapping.Fields
            .Select(ConfigurationMapper.ToDto)
            .Where(field => string.IsNullOrWhiteSpace(field.ResourceType)
                || string.Equals(field.ResourceType, mapping.ResourceType, StringComparison.OrdinalIgnoreCase))
            .Where(field => string.IsNullOrWhiteSpace(field.DestinationObject)
                || string.Equals(field.DestinationObject, mapping.DestinationObject, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var mappingNode = workflow.AddNode(
            WorkflowNodeTypes.Mapping,
            WorkflowNodeCategory.Transform,
            MappingRank,
            subRank: chainIndex,
            displayName: mapping.Name,
            configurationJson: Serialize(new Dictionary<string, object?>
            {
                ["fields"] = mappingFields,
                ["resourceType"] = mapping.ResourceType,
                ["destinationObject"] = mapping.DestinationObject
            }),
            positionX: 500,
            positionY: laneY);

        var destinationNode = workflow.AddNode(
            chain.DestinationNodeType,
            WorkflowNodeCategory.Destination,
            DestinationRank,
            subRank: chainIndex,
            displayName: chain.Destination.Name,
            // Embed the secret *reference* (vault + name) — never the resolved secret — plus the target table. The
            // destination executor rebuilds a real DestinationConfiguration from these and the writer resolves the
            // connection secret at run time via ISecretProvider, so a graph launch writes identical rows to the route path.
            configurationJson: Serialize(new Dictionary<string, object?>
            {
                ["secretKeyVaultName"] = chain.Destination.SecretReference.KeyVaultName,
                ["secretName"] = chain.Destination.SecretReference.SecretName,
                ["target"] = chain.Destination.Target,
                ["resourceType"] = mapping.ResourceType,
                ["destinationObject"] = mapping.DestinationObject,
                // The writer derives the destination table's columns from the mapping fields, so the destination
                // node needs them too (not just the mapping node) to create/align the target table.
                ["fields"] = mappingFields
            }),
            positionX: 750,
            positionY: laneY);

        workflow.AddEdge(sourceNode.Id, deIdentificationNode.Id);
        workflow.AddEdge(deIdentificationNode.Id, mappingNode.Id);
        workflow.AddEdge(mappingNode.Id, destinationNode.Id);
    }

    // Secret-free source config: identifiers + endpoints only, never the private key or client secret, so it is safe
    // to persist. A launch's cached patient-scoped token (keyed by source connection id) drives the actual pull.
    private static FhirSourceConfiguration BuildSecretFreeSourceConfiguration(
        SourceConnection source,
        string? searchParameters)
        => new(
            MapRuntimeSourceType(source.SourceSystemType),
            source.Name,
            source.BaseUrl,
            source.Authentication.TokenEndpoint,
            source.Authentication.ClientId,
            source.Authentication.KeyId,
            PrivateKeyPem: null,
            source.Authentication.Scopes,
            SearchCount: 100,
            MaxPages: 5,
            SourceConnectionId: source.Id,
            SearchParameters: searchParameters,
            ClientSecret: null,
            ApplicationType: source.ApplicationType);

    private static RuntimeSourceType MapRuntimeSourceType(SourceSystemType sourceSystemType) => sourceSystemType switch
    {
        SourceSystemType.Sample => RuntimeSourceType.Sample,
        SourceSystemType.Epic => RuntimeSourceType.Epic,
        SourceSystemType.Cerner => RuntimeSourceType.Cerner,
        SourceSystemType.Allscripts => RuntimeSourceType.Allscripts,
        SourceSystemType.Healow => RuntimeSourceType.Healow,
        SourceSystemType.MeditechGreenfield => RuntimeSourceType.MeditechGreenfield,
        SourceSystemType.Athenahealth => RuntimeSourceType.Athenahealth,
        _ => RuntimeSourceType.GenericFhir
    };

    // Only Epic + Sample source nodes are currently exposed in the catalog; other vendors reuse the Epic search
    // client, so they project onto the Epic source node (the FhirSourceConfiguration still carries the real vendor).
    private static string MapSourceNodeType(SourceSystemType sourceSystemType) => sourceSystemType switch
    {
        SourceSystemType.Sample => WorkflowNodeTypes.SampleSource,
        _ => WorkflowNodeTypes.EpicSource
    };

    // Only SqlServer + CSV destination nodes are currently exposed in the catalog; others return null and stay on
    // the route path rather than projecting onto an unsupported node.
    private static string? MapDestinationNodeType(DestinationType destinationType) => destinationType switch
    {
        DestinationType.SqlServer => WorkflowNodeTypes.SqlServerDestination,
        DestinationType.Csv => WorkflowNodeTypes.CsvDestination,
        _ => null
    };

    private static string Serialize(object value) => JsonSerializer.Serialize(value, ConfigJsonOptions);

    private sealed record ProjectedChain(
        MappingProfile Mapping,
        DestinationConfiguration Destination,
        string DestinationNodeType,
        string? SearchParameters);
}
