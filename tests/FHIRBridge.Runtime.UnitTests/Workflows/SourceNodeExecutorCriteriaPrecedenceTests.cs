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

// Covers which search criteria a workflow run filters by. The source's own Search criteria is never read any more:
// the resource type's Criteria row applies, winning per parameter over anything else in SearchParameters; on an API
// run the API's Patient criteria are combined with the resource's (API wins where both set a parameter) and nothing
// else from the source is kept. Advanced Search Options (_sort/_include/_revinclude) survive in every case.
public sealed class SourceNodeExecutorCriteriaPrecedenceTests
{
    private const string CanvasNodeId = "n-source-1";
    private static readonly Guid WorkflowId = Guid.NewGuid();

    [Fact]
    public async Task Resource_criteria_win_per_parameter_and_result_shaping_parameters_are_kept()
    {
        // "identifier=HAND-1&active=true" stands in for a hand-authored / route-projected node "searchParameters".
        var calls = await RunAsync(
            resources: "Patient",
            sourceSearchParameters: "identifier=HAND-1&active=true&_include=Patient:general-practitioner&_sort=-_lastUpdated",
            criteriaRows: [("Patient", "identifier=RES-1")]);

        calls["Patient"].SearchParameters.Should()
            .Be("identifier=RES-1&active=true&_include=Patient:general-practitioner&_sort=-_lastUpdated");
    }

    [Fact]
    public async Task Api_run_combines_api_and_resource_criteria_with_the_api_winning_a_shared_parameter()
    {
        var calls = await RunAsync(
            resources: "Patient",
            sourceSearchParameters: "identifier=HAND-1&_sort=-_lastUpdated",
            criteriaRows: [("Patient", "identifier=RES-1&gender=female")],
            apiCriteria: "identifier=API-1");

        // The connector appends PatientSearchCriteria itself, so SearchParameters carries only the resource
        // parameters the API doesn't set (gender), plus the result-shaping ones. identifier=RES-1 and the
        // hand-authored identifier=HAND-1 are both gone.
        var patientCall = calls["Patient"];
        patientCall.PatientSearchCriteria.Should().Be("identifier=API-1");
        patientCall.SearchParameters.Should().Be("gender=female&_sort=-_lastUpdated");
    }

    [Fact]
    public async Task Api_run_without_resource_criteria_drops_everything_but_result_shaping_parameters()
    {
        var calls = await RunAsync(
            resources: "Patient",
            sourceSearchParameters: "identifier=HAND-1&_sort=-_lastUpdated",
            criteriaRows: [],
            apiCriteria: "family=Smith");

        calls["Patient"].PatientSearchCriteria.Should().Be("family=Smith");
        calls["Patient"].SearchParameters.Should().Be("_sort=-_lastUpdated");
    }

    [Fact]
    public async Task Search_parameters_apply_unchanged_when_there_is_no_api_or_resource_criteria()
    {
        var calls = await RunAsync(
            resources: "Patient",
            sourceSearchParameters: "identifier=HAND-1&_sort=-_lastUpdated",
            criteriaRows: [("Observation", "category=laboratory")]);

        calls["Patient"].SearchParameters.Should().Be("identifier=HAND-1&_sort=-_lastUpdated");
    }

    [Fact]
    public async Task Resource_criteria_apply_to_a_compartment_type_fetched_without_a_cohort()
    {
        var calls = await RunAsync(
            resources: "Observation",
            sourceSearchParameters: "_sort=-date",
            criteriaRows: [("Observation", "category=laboratory&date=ge2024-01-01&date=le2024-12-31")]);

        // Repeated keys inside the criteria (a date range) are kept as authored.
        calls["Observation"].SearchParameters.Should().Be("category=laboratory&date=ge2024-01-01&date=le2024-12-31&_sort=-date");
    }

    [Fact]
    public async Task Cohort_scoped_sibling_gets_its_own_resource_criteria_with_a_date_range_intact()
    {
        var calls = await RunAsync(
            resources: "Patient, Observation",
            sourceSearchParameters: "identifier=HAND-1",
            criteriaRows: [("Observation", "category=laboratory&date=ge2024-01-01&date=le2024-12-31")]);

        calls["Patient"].SearchParameters.Should().Be("identifier=HAND-1");
        var observationCall = calls["Observation"];
        observationCall.SearchParameters.Should().Be("category=laboratory&date=ge2024-01-01&date=le2024-12-31");
        observationCall.PatientIds.Should().BeEquivalentTo(["p1"]);
    }

    [Fact]
    public async Task The_source_forms_own_search_criteria_field_is_not_read()
    {
        string? resolvedWith = "not-called";
        await RunAsync(
            resources: "Patient",
            sourceSearchParameters: "",
            criteriaRows: [],
            extraNodeConfig: "\"Search criteria\":\"identifier=OLD-1\"",
            onResolve: searchParameters => resolvedWith = searchParameters);

        resolvedWith.Should().BeNull();
    }

    [Fact]
    public async Task Criteria_are_read_for_the_workflow_the_node_belongs_to_not_a_stale_stamped_id()
    {
        // A copied workflow's nodes keep the ORIGINAL workflow's id stamped in "workflowId"; the copy's own rows
        // (keyed by its real id) must be the ones applied.
        var calls = await RunAsync(
            resources: "Patient",
            sourceSearchParameters: "",
            criteriaRows: [("Patient", "identifier=COPY-1")],
            stampedWorkflowId: Guid.NewGuid());

        calls["Patient"].SearchParameters.Should().Be("identifier=COPY-1");
    }

    [Fact]
    public async Task Caller_criteria_with_a_different_parameter_are_combined_with_the_saved_criteria()
    {
        // Regression: a caller's Patient gender=male used to REPLACE the saved identifier= criteria, so Epic got a
        // gender-only Patient search, found no patients, and every patient-scoped sibling search went out unscoped.
        var calls = await RunAsync(
            resources: "Patient",
            sourceSearchParameters: "",
            criteriaRows: [("Patient", "identifier=RES-1,RES-2")],
            callerCriteria: new Dictionary<string, string> { ["Patient"] = "gender=male" });

        calls["Patient"].SearchParameters.Should().Be("gender=male&identifier=RES-1,RES-2");
    }

    [Fact]
    public async Task A_modified_parameter_wins_over_the_same_unmodified_parameter()
    {
        var calls = await RunAsync(
            resources: "Patient",
            sourceSearchParameters: "",
            criteriaRows: [("Patient", "identifier=RES-1")],
            callerCriteria: new Dictionary<string, string> { ["Patient"] = "identifier:exact=CALLER-1" });

        calls["Patient"].SearchParameters.Should().Be("identifier:exact=CALLER-1");
    }

    [Fact]
    public async Task Caller_criteria_win_a_parameter_the_saved_criteria_also_set()
    {
        var calls = await RunAsync(
            resources: "Patient",
            sourceSearchParameters: "",
            criteriaRows: [("Patient", "identifier=RES-1&active=true")],
            callerCriteria: new Dictionary<string, string> { ["Patient"] = "identifier=CALLER-1" });

        calls["Patient"].SearchParameters.Should().Be("identifier=CALLER-1&active=true");
    }

    [Fact]
    public async Task Caller_criteria_for_one_type_leave_another_types_saved_criteria_unchanged()
    {
        var calls = await RunAsync(
            resources: "Observation",
            sourceSearchParameters: "",
            criteriaRows: [("Observation", "category=laboratory")],
            callerCriteria: new Dictionary<string, string> { ["Patient"] = "gender=male" });

        calls["Observation"].SearchParameters.Should().Be("category=laboratory");
    }

    [Fact]
    public async Task Code_filter_the_source_ignored_is_enforced_on_the_fetched_records()
    {
        // athenahealth has no Observation "status" search parameter, so it answers status=final with every status.
        WorkflowNodeOutput? output = null;
        await RunAsync(
            resources: "Observation",
            sourceSearchParameters: "",
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
        Action<WorkflowNodeOutput>? onOutput = null,
        string? extraNodeConfig = null,
        Action<string?>? onResolve = null,
        Guid? stampedWorkflowId = null,
        IReadOnlyDictionary<string, string>? callerCriteria = null)
    {
        var sourceConnectionId = Guid.NewGuid();
        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic Sandbox", "https://fhir.example.com", null, "client-1", null, null, [],
            SourceConnectionId: sourceConnectionId,
            SearchParameters: string.IsNullOrEmpty(sourceSearchParameters) ? null : sourceSearchParameters,
            PatientSearchCriteria: apiCriteria);

        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver
            .Setup(x => x.ResolveAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Callback((Guid _, string? searchParameters, string? _, CancellationToken _, string? _, string? _) => onResolve?.Invoke(searchParameters))
            .ReturnsAsync(source);
        resolver
            .Setup(x => x.ResolveWithRunOverridesAsync(sourceConnectionId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<RunSourceOverrides>()))
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
            configurationJson: $$"""{"sourceConnectionId":"{{sourceConnectionId}}","Resources":"{{resources}}","canvasNodeId":"{{CanvasNodeId}}","workflowId":"{{stampedWorkflowId ?? WorkflowId}}"{{(extraNodeConfig is null ? "" : "," + extraNodeConfig)}}}""");

        var executor = new EpicSourceNodeExecutor(
            clientFactory.Object, resolver.Object, resourceTypeCriteriaRepository: criteriaRepository.Object);

        var context = new WorkflowExecutionContext(
            Guid.NewGuid(), "corr",
            sourceOverrides: callerCriteria is null ? null : new RunSourceOverrides(null, null, callerCriteria));
        var output = await executor.ExecuteAsync(context, sourceNode, [], CancellationToken.None);
        onOutput?.Invoke(output);

        return calls;
    }
}
