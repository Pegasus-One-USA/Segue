using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Workflows;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Payloads;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FHIRBridge.SharedKernel.Enums;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Infrastructure;

// Scenario B: the route→graph projection produces a graph the validator accepts, the feature flag gates per source,
// the resolver get-or-creates a durable graph, and a projected graph carries data un-scrubbed (deIdentify:false).
public sealed class WorkflowGraphLaunchTests
{
    [Fact]
    public async Task Projection_produces_a_graph_that_passes_the_validator()
    {
        var repository = BuildRepositoryWithOneRoute(out var sourceId, DestinationType.SqlServer);
        var projection = new RouteToWorkflowGraphProjection(repository);

        var workflow = await projection.ProjectForSourceAsync(sourceId, CancellationToken.None);

        workflow.Should().NotBeNull();
        workflow!.Name.Should().Be(LaunchWorkflowNaming.ForSource(sourceId));
        workflow.Nodes.Select(node => node.NodeType).Should().BeEquivalentTo(
            WorkflowNodeTypes.EpicSource,
            WorkflowNodeTypes.DeIdentification,
            WorkflowNodeTypes.Mapping,
            WorkflowNodeTypes.SqlServerDestination);
        workflow.Edges.Should().HaveCount(3);

        var validation = new WorkflowGraphValidator().Validate(workflow);
        validation.IsValid.Should().BeTrue(because: string.Join("; ", validation.Errors));
    }

    [Fact]
    public async Task Projection_returns_null_when_destination_type_has_no_supported_node()
    {
        // Blob has no exposed catalog node yet, so the chain stays on the route path and nothing projects.
        var repository = BuildRepositoryWithOneRoute(out var sourceId, DestinationType.BlobStorage);
        var projection = new RouteToWorkflowGraphProjection(repository);

        var workflow = await projection.ProjectForSourceAsync(sourceId, CancellationToken.None);

        workflow.Should().BeNull();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Feature_flag_master_switch_gates_all_sources(bool enabled, bool expected)
    {
        var options = new WorkflowGraphExecutionOptions { Enabled = enabled };
        options.IsEnabledForSource(Guid.NewGuid()).Should().Be(expected);
    }

    [Fact]
    public void Feature_flag_allow_list_gates_specific_sources()
    {
        var allowed = Guid.NewGuid();
        var options = new WorkflowGraphExecutionOptions { Enabled = true, SourceConnectionIds = [allowed] };

        options.IsEnabledForSource(allowed).Should().BeTrue();
        options.IsEnabledForSource(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public async Task Resolver_projects_and_persists_once_then_reuses()
    {
        var repository = BuildRepositoryWithOneRoute(out var sourceId, DestinationType.SqlServer);
        var store = new InMemoryWorkflowDefinitionStore();
        var resolver = new LaunchWorkflowResolver(new RouteToWorkflowGraphProjection(repository), store);

        var first = await resolver.ResolveForSourceAsync(sourceId, CancellationToken.None);
        var second = await resolver.ResolveForSourceAsync(sourceId, CancellationToken.None);

        first.Should().NotBeNull();
        second!.Id.Should().Be(first!.Id);
        (await store.ListAsync(CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public async Task DeIdentification_node_passes_data_through_when_deIdentify_is_false()
    {
        var scrubber = new RecordingDeIdentificationService();
        var executor = new DeIdentificationNodeExecutor(scrubber);
        var node = BuildNode(WorkflowNodeTypes.DeIdentification, WorkflowNodeCategory.Compliance, 50, "{\"deIdentify\":false}");
        var inputs = new[]
        {
            new WorkflowNodeOutput(
                Guid.NewGuid(),
                WorkflowNodeTypes.EpicSource,
                new ResourceBatch([new ResourceEnvelope("Patient", "p1", "{\"resourceType\":\"Patient\"}")]),
                WorkflowDataContract.ResourceBatch)
        };

        var output = await executor.ExecuteAsync(CreateContext(), node, inputs, CancellationToken.None);

        scrubber.CallCount.Should().Be(0);
        var batch = output.Payload.Should().BeOfType<DeIdentifiedBatch>().Subject;
        var envelope = batch.Records.Should().ContainSingle().Subject.Should().BeOfType<ResourceEnvelope>().Subject;
        envelope.Payload.Should().Be("{\"resourceType\":\"Patient\"}");
    }

    [Fact]
    public async Task DeIdentification_node_scrubs_when_deIdentify_defaults_on()
    {
        var scrubber = new RecordingDeIdentificationService();
        var executor = new DeIdentificationNodeExecutor(scrubber);
        var node = BuildNode(WorkflowNodeTypes.DeIdentification, WorkflowNodeCategory.Compliance, 50, "{}");
        var inputs = new[]
        {
            new WorkflowNodeOutput(
                Guid.NewGuid(),
                WorkflowNodeTypes.EpicSource,
                new ResourceBatch([new ResourceEnvelope("Patient", "p1", "{\"resourceType\":\"Patient\"}")]),
                WorkflowDataContract.ResourceBatch)
        };

        var output = await executor.ExecuteAsync(CreateContext(), node, inputs, CancellationToken.None);

        scrubber.CallCount.Should().Be(1);
        var batch = output.Payload.Should().BeOfType<DeIdentifiedBatch>().Subject;
        var envelope = batch.Records.Should().ContainSingle().Subject.Should().BeOfType<ResourceEnvelope>().Subject;
        envelope.Payload.Should().Be("SCRUBBED");
    }

    private static WorkflowExecutionContext CreateContext() => new(Guid.NewGuid(), "test");

    private static WorkflowNode BuildNode(string nodeType, WorkflowNodeCategory category, int rank, string configurationJson)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "n", 1);
        return workflow.AddNode(nodeType, category, rank, configurationJson: configurationJson);
    }

    private static FakeConfigurationRepository BuildRepositoryWithOneRoute(out Guid sourceId, DestinationType destinationType)
    {
        var source = new SourceConnection(
            "Epic Sandbox",
            SourceSystemType.Epic,
            "https://fhir.example.org",
            new SourceAuthenticationConfiguration(
                AuthenticationType.SmartBackendServices,
                "client-id",
                "https://fhir.example.org/token",
                ["system/Patient.read"],
                clientSecret: null,
                privateKey: null,
                keyId: null));
        sourceId = source.Id;

        var destination = new DestinationConfiguration(
            "Warehouse",
            destinationType,
            new SecretReference("kv", "conn"),
            "dbo");

        var mapping = new MappingProfile(
            "Patient mapping",
            "Patient",
            source.Id,
            destination.Id,
            "Patient",
            []);

        var route = new ResourcePipelineRoute(
            webhookConfigurationId: null,
            mappingProfileId: mapping.Id,
            IngestionMode.ScheduledPull,
            scheduleExpression: null,
            searchParameters: null,
            isEnabled: true,
            priority: 0);

        return new FakeConfigurationRepository([source], [destination], [mapping], [route]);
    }


    // ── De-identification profile resolution ─────────────────────────────────────────────────────────────────
    // Regression: a DAG run never consulted the profile assigned to the destination — the only profile the UI
    // lets you pick. Nothing writes the node's "profileId" config, so every node fell through to
    // DefaultProfileId, whose row does not exist while the seeder is disabled; the rule query returned nothing
    // and resources were written unredacted while the run reported success.

    private static (DeIdentificationNodeExecutor Executor, RecordingDeIdentificationService Scrubber, Guid DestinationId, Guid ProfileId) BuildProfileResolutionSut()
    {
        var profileId = Guid.NewGuid();
        var destination = new DestinationConfiguration(
            "Warehouse", DestinationType.SqlServer, new SecretReference("kv", "secret"), "Server=.;Database=W");
        destination.SetDeIdentificationProfile(profileId);

        var repository = new FakeConfigurationRepository([], [destination], [], []);
        var scrubber = new RecordingDeIdentificationService();

        return (new DeIdentificationNodeExecutor(scrubber, configurationRepository: repository),
                scrubber, destination.Id, profileId);
    }

    private static WorkflowNodeOutput[] OnePatientInput() =>
    [
        new(Guid.NewGuid(),
            WorkflowNodeTypes.EpicSource,
            new ResourceBatch([new ResourceEnvelope("Patient", "p1", "{\"resourceType\":\"Patient\"}")]),
            WorkflowDataContract.ResourceBatch)
    ];

    [Fact]
    public async Task DeIdentification_node_resolves_the_profile_assigned_to_its_destination()
    {
        var (executor, scrubber, destinationId, profileId) = BuildProfileResolutionSut();
        // destinationId is what the portal's chain-node patch stamps onto every chain node; profileId is never stamped.
        var node = BuildNode(WorkflowNodeTypes.DeIdentification, WorkflowNodeCategory.Compliance, 50,
            $"{{\"destinationId\":\"{destinationId}\"}}");

        await executor.ExecuteAsync(CreateContext(), node, OnePatientInput(), CancellationToken.None);

        scrubber.LastProfileId.Should().Be(profileId);
        scrubber.LastProfileId.Should().NotBe(DeIdentificationProfile.DefaultProfileId,
            "falling back to the unseeded default is what silently disabled redaction");
    }

    [Fact]
    public async Task DeIdentification_node_prefers_an_explicit_profileId_over_the_destination()
    {
        var (executor, scrubber, destinationId, destinationProfileId) = BuildProfileResolutionSut();
        var explicitProfileId = Guid.NewGuid();
        var node = BuildNode(WorkflowNodeTypes.DeIdentification, WorkflowNodeCategory.Compliance, 50,
            $"{{\"destinationId\":\"{destinationId}\",\"profileId\":\"{explicitProfileId}\"}}");

        await executor.ExecuteAsync(CreateContext(), node, OnePatientInput(), CancellationToken.None);

        scrubber.LastProfileId.Should().Be(explicitProfileId).And.NotBe(destinationProfileId);
    }

    [Fact]
    public async Task DeIdentification_node_falls_back_to_the_default_profile_without_a_destination()
    {
        var (executor, scrubber, _, _) = BuildProfileResolutionSut();
        var node = BuildNode(WorkflowNodeTypes.DeIdentification, WorkflowNodeCategory.Compliance, 50, "{}");

        await executor.ExecuteAsync(CreateContext(), node, OnePatientInput(), CancellationToken.None);

        scrubber.LastProfileId.Should().Be(DeIdentificationProfile.DefaultProfileId);
    }

    private sealed class RecordingDeIdentificationService : IDeIdentificationService
    {
        public int CallCount { get; private set; }

        /// <summary>Which profile the executor resolved — the whole point of the precedence tests above.</summary>
        public Guid? LastProfileId { get; private set; }

        public Task<DeIdentificationResult> DeIdentifyAsync(DeIdentificationRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastProfileId = request.ProfileId;
            return Task.FromResult(new DeIdentificationResult("SCRUBBED", []));
        }
    }

    private sealed class FakeConfigurationRepository : IConfigurationRepository
    {
        private readonly IReadOnlyList<SourceConnection> _sources;
        private readonly IReadOnlyList<DestinationConfiguration> _destinations;
        private readonly IReadOnlyList<MappingProfile> _mappings;
        private readonly IReadOnlyList<ResourcePipelineRoute> _routes;

        public FakeConfigurationRepository(
            IReadOnlyList<SourceConnection> sources,
            IReadOnlyList<DestinationConfiguration> destinations,
            IReadOnlyList<MappingProfile> mappings,
            IReadOnlyList<ResourcePipelineRoute> routes)
        {
            _sources = sources;
            _destinations = destinations;
            _mappings = mappings;
            _routes = routes;
        }

        public Task<IReadOnlyList<SourceConnection>> GetSourceConnectionsAsync(CancellationToken ct) => Task.FromResult(_sources);
        public Task<PagedResult<SourceConnection>> GetSourceConnectionsPagedAsync(SourceConnectionFilter filter, int page, int pageSize, string? sortBy, string? sortOrder, CancellationToken ct) =>
            Task.FromResult(new PagedResult<SourceConnection>(_sources, _sources.Count, page, pageSize));
        public Task<SourceConnection?> GetSourceConnectionAsync(Guid id, CancellationToken ct) => Task.FromResult(_sources.FirstOrDefault(x => x.Id == id));
        public Task<IReadOnlyList<DestinationConfiguration>> GetDestinationsAsync(CancellationToken ct) => Task.FromResult(_destinations);
        public Task<PagedResult<DestinationConfiguration>> GetDestinationsPagedAsync(DestinationFilter filter, int page, int pageSize, string? sortBy, string? sortOrder, CancellationToken ct) =>
            Task.FromResult(new PagedResult<DestinationConfiguration>(_destinations, _destinations.Count, page, pageSize));
        public Task<DestinationConfiguration?> GetDestinationAsync(Guid id, CancellationToken ct) => Task.FromResult(_destinations.FirstOrDefault(x => x.Id == id));
        public Task<bool> HasDestinationExecutionHistoryAsync(Guid destinationId, CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlyList<MappingProfile>> GetMappingProfilesAsync(CancellationToken ct) => Task.FromResult(_mappings);
        public Task<PagedResult<MappingProfile>> GetMappingProfilesPagedAsync(MappingProfileFilter filter, int page, int pageSize, string? sortBy, string? sortOrder, CancellationToken ct) =>
            Task.FromResult(new PagedResult<MappingProfile>(_mappings, _mappings.Count, page, pageSize));
        public Task<MappingProfile?> GetMappingProfileAsync(Guid id, CancellationToken ct) => Task.FromResult(_mappings.FirstOrDefault(x => x.Id == id));
        public Task<IReadOnlyList<MappingProfile>> GetMappingProfilesByDestinationAsync(Guid destinationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MappingProfile>>(_mappings.Where(x => x.DestinationId == destinationId).ToList());
        public Task<MappingProfile?> FindMappingProfileAsync(string resourceType, Guid sourceConnectionId, Guid destinationId, CancellationToken ct) =>
            Task.FromResult(_mappings.FirstOrDefault(x =>
                x.ResourceType == resourceType && x.SourceConnectionId == sourceConnectionId && x.DestinationId == destinationId));
        public Task<IReadOnlyList<ResourcePipelineRoute>> GetRoutesAsync(CancellationToken ct) => Task.FromResult(_routes);
        public Task<ResourcePipelineRoute?> GetRouteAsync(Guid id, CancellationToken ct) => Task.FromResult(_routes.FirstOrDefault(x => x.Id == id));

        public Task<IReadOnlyList<WebhookConfiguration>> GetWebhooksAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WebhookConfiguration>>([]);
        public Task<WebhookConfiguration?> GetWebhookAsync(Guid id, CancellationToken ct) => Task.FromResult<WebhookConfiguration?>(null);

        public Task<EhrEndpoint?> GetEhrEndpointAsync(Guid id, CancellationToken ct) => Task.FromResult<EhrEndpoint?>(null);

        public Task<IConfigurationTransaction> BeginTransactionAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AddSourceConnectionAsync(SourceConnection e, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateSourceConnectionAsync(SourceConnection e, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteSourceConnectionAsync(SourceConnection sourceConnection, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ExistsWithNameAsync(string name, Guid? excludeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SourceConfiguration>> GetSourceConfigurationsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<SourceConfiguration?> GetSourceConfigurationAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task AddSourceConfigurationAsync(SourceConfiguration e, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateSourceConfigurationAsync(SourceConfiguration e, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteSourceConfigurationAsync(SourceConfiguration sourceConfiguration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AddDestinationAsync(DestinationConfiguration e, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateDestinationAsync(DestinationConfiguration e, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveDestinationAsync(DestinationConfiguration e, CancellationToken ct) => throw new NotSupportedException();
        public Task AddMappingProfileAsync(MappingProfile e, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateMappingProfileAsync(MappingProfile e, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveMappingProfileAsync(MappingProfile e, CancellationToken ct) => throw new NotSupportedException();
        public Task AddRouteAsync(ResourcePipelineRoute e, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateRouteAsync(ResourcePipelineRoute e, CancellationToken ct) => throw new NotSupportedException();
        public Task AddWebhookAsync(WebhookConfiguration e, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateWebhookAsync(WebhookConfiguration e, CancellationToken ct) => throw new NotSupportedException();
    }
}
