using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Infrastructure.Workflows.EhrWrite;
using FHIRBridge.Runtime.Infrastructure.Workflows.Executors;
using FluentAssertions;
using Moq;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

/// <summary>
/// The executor resolves the EHR from the NODE's own dest_sourceConnectionId (not the upstream source), refuses a
/// connection that may not be written to, and hands the writer a channel carrying the node's options.
/// </summary>
public sealed class EhrWriteBackDestinationNodeExecutorTests
{
    private static readonly Guid TargetId = Guid.NewGuid();

    [Fact]
    public void A_read_by_id_never_carries_the_bulk_only_group_scope()
    {
        // eCW refuses a token with system/Group.read on REST reads: the note Binary read got 401 "No valid token".
        DestinationNodeExecutor.WithoutBulkGroupScope(
                ["system/DocumentReference.read", "system/Binary.r", "system/Group.read", "system/Patient.read"])
            .Should().Equal("system/DocumentReference.read", "system/Binary.r", "system/Patient.read");
    }

    private static SourceConnection Connection(SourceSystemType vendor = SourceSystemType.Epic, SourceConnectionAccess access = SourceConnectionAccess.Write) =>
        new("Epic write", vendor, "https://fhir.epic.com/R4", new SourceAuthenticationConfiguration(AuthenticationType.SmartBackendServices, "client", "https://auth/token", [], null, null, "kid"), access: access);

    private static (EhrWriteBackDestinationNodeExecutor Executor, List<PipelineWriteContext> Contexts, Mock<IFhirWriteClient> WriteClient) Build(
        SourceConnection? connection,
        List<MappedDestinationRecord>? writtenRecords = null,
        DestinationWriteResult? writeResult = null)
    {
        var contexts = new List<PipelineWriteContext>();
        var writer = new Mock<IConfiguredDestinationWriter>();
        writer.Setup(w => w.WriteAsync(
                It.IsAny<DestinationConfiguration>(), It.IsAny<MappingProfile>(),
                It.IsAny<IReadOnlyCollection<MappedDestinationRecord>>(), It.IsAny<PipelineWriteContext>(), It.IsAny<CancellationToken>()))
            .Callback<DestinationConfiguration, MappingProfile, IReadOnlyCollection<MappedDestinationRecord>, PipelineWriteContext, CancellationToken>(
                (_, _, records, context, _) =>
                {
                    contexts.Add(context);
                    writtenRecords?.AddRange(records);
                })
            .ReturnsAsync(writeResult ?? new DestinationWriteResult(0));
        var writerFactory = new Mock<IConfiguredDestinationWriterFactory>();
        writerFactory.Setup(f => f.Create(DestinationType.EhrWriteBack)).Returns(writer.Object);

        var configuration = new Mock<IConfigurationRepository>();
        configuration.Setup(r => r.GetSourceConnectionAsync(TargetId, It.IsAny<CancellationToken>())).ReturnsAsync(connection);

        var source = new FhirSourceConfiguration(
            RuntimeSourceType.Epic, "Epic write", "https://fhir.epic.com/R4", "https://auth/token", "client", null, null,
            ["system/Patient.rs"], SourceConnectionId: TargetId, SearchParameters: "_count=10");
        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        resolver.Setup(r => r.ResolveAsync(TargetId, null, null, It.IsAny<CancellationToken>(), null, null)).ReturnsAsync(source);

        var writeClient = new Mock<IFhirWriteClient>();
        var sourceClient = writeClient.As<IFhirSourceClient>();
        var clientFactory = new Mock<IFhirSourceClientFactory>();
        clientFactory.Setup(f => f.Create(RuntimeSourceType.Epic)).Returns(sourceClient.Object);

        var executor = new EhrWriteBackDestinationNodeExecutor(
            writerFactory.Object,
            configurationRepository: configuration.Object,
            sourceClientFactory: clientFactory.Object,
            sourceConnectionResolver: resolver.Object);
        return (executor, contexts, writeClient);
    }

    private static WorkflowNode Node(string configurationJson)
    {
        var workflow = new WorkflowDefinition(Guid.NewGuid(), "write-back", 1);
        return workflow.AddNode(WorkflowNodeTypes.EhrWriteBackDestination, WorkflowNodeCategory.Destination, 70, configurationJson: configurationJson);
    }

    [Fact]
    public async Task A_test_run_is_reported_as_one()
    {
        // The run report must say a test run went to the FHIR test server, not "Live run to Epic".
        var report = new EhrWriteReport(false, "Epic", 0, "test-server", [], TestRun: true);
        var (executor, _, _) = Build(Connection(), writeResult: new DestinationWriteResult(0, EhrWrite: report));
        var node = Node($$"""{"dest_sourceConnectionId":"{{TargetId}}","destinationId":"{{Guid.NewGuid()}}","dest_resources":"Condition"}""");

        var output = await executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        output.Payload.Should().BeOfType<FHIRBridge.Runtime.Application.Workflows.Payloads.DestinationWriteResult>()
            .Which.EhrWrite!.TestRun.Should().BeTrue();
    }

    [Fact]
    public async Task Attaches_a_channel_over_the_target_connection_with_the_nodes_options()
    {
        var destinationId = Guid.NewGuid();
        var (executor, contexts, _) = Build(Connection());
        var node = Node($$"""
            {"dest_sourceConnectionId":"{{TargetId}}","destinationId":"{{destinationId}}","dest_dryRun":"true",
             "dest_createPatientIfMissing":"true","dest_maxWritesPerRun":"25","dest_resources":"AllergyIntolerance,Condition",
             "dest_cloneMode":"true"}
            """);

        await executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        var channel = contexts.Should().ContainSingle().Subject.EhrWriteChannel;
        channel.Should().NotBeNull();
        channel!.TargetConnectionId.Should().Be(TargetId);
        channel.TargetVendor.Should().Be(SourceSystemType.Epic);
        channel.DestinationId.Should().Be(destinationId);
        channel.Options.DryRun.Should().BeTrue();
        channel.Options.CreatePatientIfMissing.Should().BeTrue();
        channel.Options.MaxWritesPerRun.Should().Be(25);
        channel.Options.ResourceTypes.Should().Equal("AllergyIntolerance", "Condition");
        channel.Options.CloneMode.Should().BeTrue();
    }

    [Fact]
    public async Task A_saved_dry_run_runs_as_a_dry_run_whatever_the_dry_run_setting_says()
    {
        // EhrWriteBack:DryRunEnabled only decides whether the portal offers Dry run. The executor takes no settings,
        // so turning it off can never turn a saved dry run into a live write.
        typeof(EhrWriteBackDestinationNodeExecutor).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .Should().NotContain(typeof(FHIRBridge.Application.Abstractions.Caching.ISystemSettingsCache))
            .And.NotContain(typeof(ISystemSettingRepository));

        var (executor, contexts, _) = Build(Connection());
        var node = Node($$"""{"dest_sourceConnectionId":"{{TargetId}}","dest_dryRun":"true","dest_resources":"AllergyIntolerance"}""");

        await executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        contexts.Single().EhrWriteChannel!.Options.DryRun.Should().BeTrue();
    }

    [Theory]
    [InlineData("DocumentReference,Patient", true)]
    [InlineData("Condition,Patient", false)]
    public async Task Binaries_reach_the_writer_only_when_notes_are_selected(string selected, bool binaryPassed)
    {
        // A note's text sits in the Binary a bulk export delivers; the strict Data-groups filter used to drop it.
        var records = new List<MappedDestinationRecord>();
        var (executor, _, _) = Build(Connection(), records);
        var node = Node($$"""{"dest_sourceConnectionId":"{{TargetId}}","dest_resources":"{{selected}}"}""");
        var upstream = new WorkflowNodeOutput(
            Guid.NewGuid(),
            WorkflowNodeTypes.EClinicalWorksSource,
            new FHIRBridge.Runtime.Application.Workflows.Payloads.ResourceBatch(
            [
                new FHIRBridge.Runtime.Application.Workflows.Payloads.ResourceEnvelope("Patient", "p1", """{"resourceType":"Patient","id":"p1"}"""),
                new FHIRBridge.Runtime.Application.Workflows.Payloads.ResourceEnvelope("DocumentReference", "n1", """{"resourceType":"DocumentReference","id":"n1"}"""),
                new FHIRBridge.Runtime.Application.Workflows.Payloads.ResourceEnvelope("Binary", "b1", """{"resourceType":"Binary","id":"b1"}"""),
            ]),
            WorkflowDataContract.ResourceBatch);

        await executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [upstream], CancellationToken.None);

        records.Select(r => r.ResourceType).Contains("Binary").Should().Be(binaryPassed);
    }

    [Fact]
    public async Task Missing_or_garbled_options_fall_to_the_safe_side()
    {
        var (executor, contexts, _) = Build(Connection());
        var node = Node($$"""{"dest_sourceConnectionId":"{{TargetId}}","dest_dryRun":"maybe","dest_maxWritesPerRun":"-4","dest_noteDocStatus":"signed"}""");

        await executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        var options = contexts.Single().EhrWriteChannel!.Options;
        options.DryRun.Should().BeTrue();
        options.CreatePatientIfMissing.Should().BeFalse();
        options.MaxWritesPerRun.Should().Be(EhrWriteBackRunOptions.DefaultMaxWritesPerRun);
        options.NoteDocStatus.Should().Be("preliminary");
        options.CloneMode.Should().BeFalse();
        options.CreateHolderEncounter.Should().BeFalse();
        options.TargetProviderId.Should().BeNull();
        options.TargetDepartmentId.Should().BeNull();
        contexts.Single().EhrWriteChannel!.VendorWriteApisActivated.Should().BeFalse();
    }

    [Fact]
    public async Task The_vendor_options_and_the_connections_activation_reach_the_channel()
    {
        var connection = Connection();
        connection.SetVendorWriteApisActivated(true);
        var (executor, contexts, _) = Build(connection);
        var node = Node($$"""
            {"dest_sourceConnectionId":"{{TargetId}}","dest_createHolderEncounter":"true",
             "dest_targetProviderId":" 71 ","dest_targetDepartmentId":"150"}
            """);

        await executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        var channel = contexts.Single().EhrWriteChannel!;
        channel.VendorWriteApisActivated.Should().BeTrue();
        channel.Options.CreateHolderEncounter.Should().BeTrue();
        channel.Options.TargetProviderId.Should().Be("71");
        channel.Options.TargetDepartmentId.Should().Be("150");
    }

    [Theory]
    [InlineData(null, "150")]
    [InlineData("", "150")]
    [InlineData("2", "2")]
    public async Task The_department_is_the_nodes_own_when_set_else_the_connections(string? nodeDepartment, string expected)
    {
        // Saved workflows that carry dest_targetDepartmentId keep it; a node without one uses the write connection's.
        var connection = Connection();
        connection.SetDepartmentId("150");
        var (executor, contexts, _) = Build(connection);
        var node = Node(nodeDepartment is null
            ? $$"""{"dest_sourceConnectionId":"{{TargetId}}"}"""
            : $$"""{"dest_sourceConnectionId":"{{TargetId}}","dest_targetDepartmentId":"{{nodeDepartment}}"}""");

        await executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        contexts.Single().EhrWriteChannel!.Options.TargetDepartmentId.Should().Be(expected);
    }

    [Fact]
    public async Task No_department_on_the_node_or_the_connection_leaves_it_unset()
    {
        var (executor, contexts, _) = Build(Connection());
        var node = Node($$"""{"dest_sourceConnectionId":"{{TargetId}}"}""");

        await executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        contexts.Single().EhrWriteChannel!.Options.TargetDepartmentId.Should().BeNull();
    }

    [Fact]
    public async Task Read_only_connection_is_refused()
    {
        var (executor, contexts, _) = Build(Connection(access: SourceConnectionAccess.Read));
        var node = Node($$"""{"dest_sourceConnectionId":"{{TargetId}}"}""");

        var act = () => executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*read-only*");
        contexts.Should().BeEmpty();
    }

    [Fact]
    public async Task Vendor_without_write_capability_is_refused()
    {
        var (executor, _, _) = Build(Connection(vendor: SourceSystemType.Cerner));
        var node = Node($$"""{"dest_sourceConnectionId":"{{TargetId}}"}""");

        var act = () => executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*does not accept EHR write-back*");
    }

    [Theory]
    [InlineData("Healow", typeof(EcwEhrWriteChannel))]
    [InlineData("Athenahealth", typeof(AthenaOneEhrWriteChannel))]
    [InlineData("Epic", typeof(FhirClientEhrWriteChannel))]
    public async Task A_test_run_builds_the_tested_vendors_channel_over_the_generic_fhir_connection(string vendor, Type channelType)
    {
        var (executor, contexts, _) = Build(Connection(vendor: SourceSystemType.GenericFhir));
        var node = Node($$"""
            {"dest_sourceConnectionId":"{{TargetId}}","dest_testAsVendor":"{{vendor}}","dest_enabledVariants":"lines-drains-airways, radiotherapy-volume"}
            """);

        await executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        var channel = contexts.Single().EhrWriteChannel!;
        channel.Should().BeOfType(channelType);
        channel.TargetVendor.ToString().Should().Be(vendor);
        channel.Options.IsTestRun.Should().BeTrue();
        channel.Options.EnabledVariants.Should().Equal("lines-drains-airways", "radiotherapy-volume");
    }

    [Fact]
    public async Task A_test_run_never_writes_to_a_real_ehr()
    {
        var (executor, contexts, _) = Build(Connection(vendor: SourceSystemType.Epic));
        var node = Node($$"""{"dest_sourceConnectionId":"{{TargetId}}","dest_testAsVendor":"Epic"}""");

        var act = () => executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*needs a Generic FHIR connection as the test server*");
        contexts.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Cerner")]
    [InlineData("GenericFhir")]
    [InlineData("epic,healow")]
    public async Task A_test_run_as_a_vendor_that_cannot_be_tested_fails_instead_of_writing(string vendor)
    {
        var (executor, contexts, _) = Build(Connection(vendor: SourceSystemType.GenericFhir));
        var node = Node($$"""{"dest_sourceConnectionId":"{{TargetId}}","dest_testAsVendor":"{{vendor}}"}""");

        var act = () => executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not a vendor that can be tested*");
        contexts.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_target_connection_is_refused()
    {
        var (executor, _, _) = Build(connection: null);
        var node = Node($$"""{"dest_sourceConnectionId":"{{TargetId}}"}""");

        var act = () => executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no longer exists*");
    }

    [Fact]
    public async Task Without_its_dependencies_the_node_fails_instead_of_faking_success()
    {
        var executor = new EhrWriteBackDestinationNodeExecutor();
        var node = Node($$"""{"dest_sourceConnectionId":"{{TargetId}}"}""");

        var act = () => executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not wired*");
    }

    [Fact]
    public async Task Epic_write_channel_sends_no_scope_and_none_of_the_source_search_settings()
    {
        var (executor, contexts, writeClient) = Build(Connection());
        FhirSourceConfiguration? used = null;
        writeClient
            .Setup(c => c.SearchForPatientAsync("Encounter", "p1", It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, FhirSourceConfiguration, CancellationToken>((_, _, source, _) => used = source)
            .ReturnsAsync(new FhirSearchPage(true, 200, [], []));
        var node = Node($$"""{"dest_sourceConnectionId":"{{TargetId}}"}""");

        await executor.ExecuteAsync(new WorkflowExecutionContext(Guid.NewGuid(), "corr"), node, [], CancellationToken.None);
        await contexts.Single().EhrWriteChannel!.SearchForPatientAsync("Encounter", "p1", CancellationToken.None);

        used.Should().NotBeNull();
        used!.OmitScopeParameter.Should().BeTrue();
        used.Scopes.Should().BeEmpty();
        used.SearchParameters.Should().BeNull();
    }
}
