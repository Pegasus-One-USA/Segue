using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using Moq;
using ResourceEnvelope = FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

// Covers which ONE criteria a search filters by: API criteria (the run request's Patient search criteria) first,
// then the resource type's own Criteria row, then the source node / connection "Search criteria". The winner
// replaces the lower tiers; Advanced Search Options (_sort/_include/_revinclude) survive whichever tier wins.
public sealed class SourceNodeExecutorCriteriaPrecedenceTests
{
    private const string CanvasNodeId = "n-source-1";
    private static readonly Guid WorkflowId = Guid.NewGuid();

    [Fact]
    public async Task Resource_criteria_replaces_the_source_criteria_and_keeps_result_shaping_parameters()
    {
        var calls = await RunAsync(
            resources: "Patient",
            sourceSearchParameters: "identifier=SRC-1&_include=Patient:general-practitioner&_sort=-_lastUpdated",
            criteriaRows: [("Patient", "identifier=RES-1")]);

        calls["Patient"].SearchParameters.Should()
            .Be("identifier=RES-1&_include=Patient:general-practitioner&_sort=-_lastUpdated");
    }

    [Fact]
    public async Task Api_criteria_outranks_both_resource_and_source_criteria()
    {
        var calls = await RunAsync(
            resources: "Patient",
            sourceSearchParameters: "identifier=SRC-1&_sort=-_lastUpdated",
            criteriaRows: [("Patient", "identifier=RES-1")],
            apiCriteria: "identifier=API-1");

        // The connector appends PatientSearchCriteria to the query itself; neither lower tier may ride along.
        var patientCall = calls["Patient"];
        patientCall.PatientSearchCriteria.Should().Be("identifier=API-1");
        patientCall.SearchParameters.Should().Be("_sort=-_lastUpdated");
    }

    [Fact]
    public async Task Source_criteria_applies_unchanged_when_there_is_no_api_or_resource_criteria()
    {
        var calls = await RunAsync(
            resources: "Patient",
            sourceSearchParameters: "identifier=SRC-1&_sort=-_lastUpdated",
            criteriaRows: [("Observation", "category=laboratory")]);

        calls["Patient"].SearchParameters.Should().Be("identifier=SRC-1&_sort=-_lastUpdated");
    }

    [Fact]
    public async Task Resource_criteria_replaces_the_source_criteria_for_a_compartment_type_fetched_without_a_cohort()
    {
        var calls = await RunAsync(
            resources: "Observation",
            sourceSearchParameters: "identifier=SRC-1",
            criteriaRows: [("Observation", "category=laboratory&date=ge2024-01-01&date=le2024-12-31")]);

        // Repeated keys inside the winning criteria (a date range) are kept as authored.
        calls["Observation"].SearchParameters.Should().Be("category=laboratory&date=ge2024-01-01&date=le2024-12-31");
    }

    [Fact]
    public async Task Cohort_scoped_sibling_still_gets_only_its_own_resource_criteria()
    {
        var calls = await RunAsync(
            resources: "Patient, Observation",
            sourceSearchParameters: "identifier=SRC-1",
            criteriaRows: [("Observation", "category=laboratory")]);

        calls["Patient"].SearchParameters.Should().Be("identifier=SRC-1");
        var observationCall = calls["Observation"];
        observationCall.SearchParameters.Should().Be("category=laboratory");
        observationCall.PatientIds.Should().BeEquivalentTo(["p1"]);
    }

    [Fact]
    public async Task Code_filter_the_source_ignored_is_enforced_on_the_fetched_records()
    {
        // athenahealth has no Observation "status" search parameter, so it answers status=final with every status.
        WorkflowNodeOutput? output = null;
        await RunAsync(
            resources: "Observation",
            sourceSearchParameters: "identifier=SRC-1",
            criteriaRows: [("Observation", "status=final")],
            respond: type =>
            [
                new ResourceEnvelope(type, "o1", """{"resourceType":"Observation","id":"o1","status":"final"}""", null, null),
                new ResourceEnvelope(type, "o2", """{"resourceType":"Observation","id":"o2","status":"unknown"}""", null, null),
                new ResourceEnvelope(type, "o3", """{"resourceType":"Observation","id":"o3","status":"entered-in-error"}""", null, null),
            ],
            onOutput: result => output = result);

        output!.Payload.Should().BeOfType<FHIRBridge.Runtime.Application.Workflows.Payloads.ResourceBatch>()
            .Which.Resources.Select(r => r.ResourceId).Should().Equal("o1");
    }

    private static async Task<Dictionary<string, FhirSourceConfiguration>> RunAsync(
        string resources,
        string sourceSearchParameters,
        IReadOnlyList<(string ResourceType, string Criteria)> criteriaRows,
        string? apiCriteria = null,
        Func<string, IReadOnlyList<ResourceEnvelope>>? respond = null,
        Action<WorkflowNodeOutput>? onOutput = null)
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId,
            SearchParameters: sourceSearchParameters,
            PatientSearchCriteria: apiCriteria);

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(source);

        var calls = new Dictionary<string, FhirSourceConfiguration>(StringComparer.OrdinalIgnoreCase);
        var client = new Mock<IFhirSourceClient>();
        client
            .Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .Returns((string type, FhirSourceConfiguration config, CancellationToken _) =>
            {
                calls[type] = config;
                if (respond is not null)
                {
                    return Task.FromResult(respond(type));
                }

                var id = string.Equals(type, "Patient", StringComparison.OrdinalIgnoreCase) ? "p1" : $"{type}-1";
                return Task.FromResult<IReadOnlyList<ResourceEnvelope>>([new ResourceEnvelope(type, id, "{}", null, null)]);
            });

        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(x => x.Create(RuntimeSourceType.Epic)).Returns(client.Object);

        var criteriaRepository = new Mock<IResourceTypeCriteriaRepository>();
        criteriaRepository
            .Setup(x => x.ListForWorkflowAsync(WorkflowId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(criteriaRows
                .Select(row => new ResourceTypeCriteria(WorkflowId, CanvasNodeId, row.ResourceType, row.Criteria))
                .ToList());

        var workflow = new WorkflowDefinition(WorkflowId, "criteria-precedence-test", 1);
        var sourceNode = workflow.AddNode(
            WorkflowNodeTypes.EpicSource,
            WorkflowNodeCategory.Source,
            rank: 0,
            configurationJson: $$"""{"sourceConnectionId":"{{sourceConnectionId}}","Resources":"{{resources}}","canvasNodeId":"{{CanvasNodeId}}","workflowId":"{{WorkflowId}}"}""");

        var executor = new EpicSourceNodeExecutor(
            clientFactory.Object, resolver.Object, resourceTypeCriteriaRepository: criteriaRepository.Object);

        var output = await executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), sourceNode, [], CancellationToken.None);
        onOutput?.Invoke(output);

        return calls;
    }
}
