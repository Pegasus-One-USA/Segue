using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Workflows;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FHIRBridge.UnitTests.Infrastructure;

/// <summary>
/// Covers the downloadable workflow-configuration export: that it reaches every configuration table the
/// workflow's graph references (the "no table should be missed" requirement), prints each row point-wise
/// alongside the SQL that selected it, and never writes a secret value into the file.
/// </summary>
public sealed class WorkflowConfigurationExporterTests
{
    // Uses the suite-wide root rather than a new one: an extra root is an extra EF internal service provider,
    // and this suite is at the >20 warn-as-error ceiling — a new root here fails an unrelated test elsewhere.
    // See SharedInMemoryDatabase. Isolation comes from the unique database name, shared across this class's
    // Facts for the same reason WorkflowSqlStoreTests shares its own.
    private static readonly string _databaseName = SharedInMemoryDatabase.NewDatabaseName();

    private static FHIRBridgeDbContext CreateContext() =>
        new(SharedInMemoryDatabase.Options<FHIRBridgeDbContext>(_databaseName));

    private static SourceAuthenticationConfiguration Auth =>
        new(AuthenticationType.None, null, null, [], null, null, null);

    /// <summary>
    /// Seeds a complete workflow: a source connection, a destination, a mapping profile between them, and a
    /// graph whose nodes reference all three — the shape the export is meant to follow end to end.
    /// </summary>
    private static async Task<(Guid WorkflowId, Guid SourceId, Guid DestinationId, Guid MappingProfileId)> SeedAsync()
    {
        await using var context = CreateContext();

        var source = new SourceConnection("Epic Prod", SourceSystemType.Epic, "https://fhir.example.org", Auth);
        var destination = new DestinationConfiguration(
            "Warehouse", DestinationType.SqlServer, new SecretReference("kv-prod", "warehouse-password"), "dbo");
        // A real field mapping, so the owned MappingFields table is genuinely exercised rather than empty.
        var mappingProfile = new MappingProfile(
            "Patient → dbo.Patient", "Patient", source.Id, destination.Id, "dbo.Patient",
            [new MappingField("MRN", "$.identifier[0].value", MappingValueType.String, true, null, null)]);

        // Free-form mapping JSON stored as a plain string column.
        mappingProfile.SetMappingJson("""{"upsertKeys":["MRN"],"nested":{"strategy":"merge"}}""");

        // A SourceConfiguration whose LastSuccessfulSyncUtcByResourceType column is a Dictionary in CLR but a
        // JSON document in the database (EF value converter) — the case that distinguishes reading the CLR value
        // from reading what the column actually stores.
        var sourceConfiguration = new SourceConfiguration(
            source.Id, "Epic retrieval", ["system/Patient.read"],
            new SourceRetrievalConfiguration(
                "search-rest", ["Patient", "Observation"], null, true,
                lastSuccessfulSyncUtcByResourceType: new Dictionary<string, DateTime>
                {
                    ["Patient"] = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                }));

        // A route, so the owned ResourcePipelineRouteMappings table is reached through it.
        var route = new ResourcePipelineRoute(
            null, mappingProfile.Id, IngestionMode.ScheduledPull, "0 * * * *", null, true, 1);

        // Stamped explicitly: in the real app the audit interceptor fills these in on SaveChanges, but this test
        // drives a bare DbContext, and CreatedBy is a required column.
        var now = DateTime.UtcNow;
        source.ApplyCreated("test@example.com", now);
        destination.ApplyCreated("test@example.com", now);
        mappingProfile.ApplyCreated("test@example.com", now);
        route.ApplyCreated("test@example.com", now);
        sourceConfiguration.ApplyCreated("test@example.com", now);

        context.SourceConnections.Add(source);
        context.DestinationConfigurations.Add(destination);
        context.MappingProfiles.Add(mappingProfile);
        context.ResourcePipelineRoutes.Add(route);
        context.SourceConfigurations.Add(sourceConfiguration);

        var workflowId = Guid.NewGuid();
        var workflow = new WorkflowDefinition(workflowId, "Epic to Warehouse", 1);
        var sourceNode = workflow.AddNode(
            "EpicSourceNode", WorkflowNodeCategory.Source, 0,
            configurationJson: $$"""{"sourceConnectionId":"{{source.Id}}","password":"hunter2"}""");
        var transformNode = workflow.AddNode(
            "MappingNode", WorkflowNodeCategory.Transform, 20,
            configurationJson: $$$"""{"mappingProfileIds":{"Patient":"{{{mappingProfile.Id}}}"}}""");
        var destinationNode = workflow.AddNode(
            "SqlServerDestinationNode", WorkflowNodeCategory.Destination, 30,
            configurationJson: $$"""{"destinationId":"{{destination.Id}}"}""");
        workflow.AddEdge(sourceNode.Id, transformNode.Id);
        workflow.AddEdge(transformNode.Id, destinationNode.Id);

        // Normally stamped by SqlWorkflowDefinitionStore.SaveAsync, which this test bypasses.
        workflow.StampAudit(now, "test@example.com", null, null);

        context.WorkflowDefinitions.Add(workflow);
        await context.SaveChangesAsync();

        return (workflowId, source.Id, destination.Id, mappingProfile.Id);
    }

    [Fact]
    public async Task Export_reaches_every_configuration_table_the_graph_references()
    {
        var seeded = await SeedAsync();

        await using var context = CreateContext();
        var export = await new WorkflowConfigurationExporter(context)
            .ExportAsync(seeded.WorkflowId, CancellationToken.None);

        export.Should().NotBeNull();
        var content = export!.Content;

        // Every table the report is contracted to cover gets its own section, whether or not it has rows —
        // an empty section is itself the answer to "does this workflow use that table".
        foreach (var table in new[]
        {
            "WorkflowDefinitions", "WorkflowNodes", "WorkflowEdges",
            "SourceConnections", "SourceConfigurations", "SourceCapabilityProfiles",
            "WebhookConfigurations", "DestinationConfigurations", "MappingProfiles",
            "ResourcePipelineRoutes", "DeIdentificationProfiles", "TransformationRules", "SchemaMappings",
            // Owned-collection tables — real tables in their own right, easy to miss because they hang off a
            // parent aggregate rather than appearing as a DbSet.
            "MappingFields", "ResourcePipelineRouteMappings",
        })
        {
            content.Should().Contain($"TABLE: {table}", $"the export must not miss the {table} table");
        }

        // The rows the graph actually points at were followed and resolved, not just named.
        content.Should().Contain("Epic to Warehouse");
        content.Should().Contain("Epic Prod");
        content.Should().Contain("Warehouse");
        content.Should().Contain(seeded.MappingProfileId.ToString());
    }

    [Fact]
    public async Task Export_writes_the_select_statement_and_point_wise_rows_for_each_table()
    {
        var seeded = await SeedAsync();

        await using var context = CreateContext();
        var export = await new WorkflowConfigurationExporter(context)
            .ExportAsync(seeded.WorkflowId, CancellationToken.None);

        var content = export!.Content;

        // Requirement 2: each section carries the query that produced it...
        content.Should().Contain("-- SQL ");
        content.Should().Contain($"SELECT * FROM WorkflowDefinitions WHERE Id = '{seeded.WorkflowId}';");
        content.Should().Contain($"SELECT * FROM SourceConnections WHERE Id IN ('{seeded.SourceId}');");

        // ...and its data printed one point per line, keyed by the real column name.
        content.Should().MatchRegex(@"Name\s+: Epic to Warehouse");
        content.Should().MatchRegex(@"BaseUrl\s+: https://fhir\.example\.org");

        // Nested node configuration is flattened into its own data points rather than dumped as one raw line.
        content.Should().Contain("mappingProfileIds.Patient");
    }

    [Fact]
    public async Task Export_expands_every_json_column_into_its_own_data_points()
    {
        var seeded = await SeedAsync();

        await using var context = CreateContext();
        var export = await new WorkflowConfigurationExporter(context)
            .ExportAsync(seeded.WorkflowId, CancellationToken.None);

        var content = export!.Content;

        // A plain string JSON column (MappingProfile.MappingJson) is flattened path-wise, not dumped as one line.
        content.Should().Contain("upsertKeys[0]");
        content.Should().Contain("nested.strategy");

        // A column whose CLR value is a Dictionary but whose STORED value is a JSON document (EF value
        // converter) must print as the JSON it really is, keeping the key → value structure. Reading only the
        // CLR side would flatten it to a bare "[Patient, 09/01/2026 ...]" pair and lose the key.
        content.Should().Contain("RetrievalLastSuccessfulSyncByResourceType");
        content.Should().MatchRegex(@"RetrievalLastSuccessfulSyncByResourceType[\s\S]{0,200}?Patient\s*:");

        // A string[] column that is NOT converted still reads sensibly rather than printing its type name.
        content.Should().NotContain("System.String[]");
    }

    [Fact]
    public async Task Export_redacts_secret_values_but_keeps_the_key_vault_reference()
    {
        var seeded = await SeedAsync();

        await using var context = CreateContext();
        var export = await new WorkflowConfigurationExporter(context)
            .ExportAsync(seeded.WorkflowId, CancellationToken.None);

        var content = export!.Content;

        // The secret VALUE never reaches the file, even though it was present in the node's configuration JSON.
        content.Should().NotContain("hunter2");
        content.Should().Contain("redacted");

        // The Key Vault REFERENCE is what makes the export useful for analysis, so it is kept.
        content.Should().Contain("kv-prod");
        content.Should().Contain("warehouse-password");
    }

    [Fact]
    public async Task Export_returns_null_for_a_workflow_that_does_not_exist()
    {
        await using var context = CreateContext();

        var export = await new WorkflowConfigurationExporter(context)
            .ExportAsync(Guid.NewGuid(), CancellationToken.None);

        export.Should().BeNull();
    }

    /// <summary>
    /// A V2-authored rule is bound by <see cref="TransformationRule.AttachToWorkflow"/>, which stores the
    /// WORKFLOW DEFINITION id in ResourcePipelineRouteId — a V2 pipeline has no ResourcePipelineRoutes row at
    /// all. The export used to filter that column against route ids only, so every rule on a V2 workflow fell
    /// outside the filter and the TransformationRules section came out empty while the rules were live and
    /// running — exactly the configuration an analyst downloads this file to read.
    /// </summary>
    [Fact]
    public async Task Export_includes_rules_bound_to_the_workflow_id_rather_than_a_route()
    {
        var (workflowId, _, _, _) = await SeedAsync();

        await using (var seed = CreateContext())
        {
            var rule = new TransformationRule(
                TransformScope.Workflow,
                TransformNodeType.HumanNameParsing,
                """{"pattern":"FirstLast"}""",
                resourceType: "Patient",
                destinationField: "HumanNameParsingGivenName",
                // No ResourcePipelineRoutes row exists for this id — it is the workflow's own id.
                resourcePipelineRouteId: workflowId,
                sourceField: "Patient.name.text");
            rule.MarkCreated("tests");
            seed.TransformationRules.Add(rule);
            await seed.SaveChangesAsync();
        }

        await using var context = CreateContext();
        var export = await new WorkflowConfigurationExporter(context)
            .ExportAsync(workflowId, CancellationToken.None);

        export.Should().NotBeNull();
        export!.Content.Should().Contain("HumanNameParsingGivenName");
        export.Content.Should().Contain("Patient.name.text");
    }
}
