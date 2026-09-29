using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Governance;
using FHIRBridge.Infrastructure.Destinations;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Destinations;

/// <summary>
/// The decorator is the guaranteed floor for Destination Activity: because
/// <see cref="ConfiguredDestinationWriterFactory.Create"/> wraps every registration, one Complete row is written
/// for every destination type — including the file/stream writers that have no connection to report — without
/// each writer repeating the call.
/// <para>The recurring theme below is that this decorator <em>observes</em>: it must never change what the write
/// returns, what it throws, or whether it succeeds.</para>
/// </summary>
public sealed class LoggingConfiguredDestinationWriterTests
{
    private static DestinationConfiguration Destination() =>
        new("Warehouse", DestinationType.DataFabricWarehouse, new SecretReference("kv", "secret"), "dbo.Patient", null);

    private static MappingProfile Mapping() =>
        new("Patient", "Patient", Guid.NewGuid(), Guid.NewGuid(), "dbo.Patient", []);

    private static MappedDestinationRecord Record() =>
        new(Guid.NewGuid(), "Patient", "dbo.Patient", "123", new Dictionary<string, object?> { ["Name"] = "Alice" });

    private static PipelineWriteContext Context() =>
        new(false, "Nightly Load", DateTimeOffset.UtcNow, CorrelationId: "corr-1");

    private static (LoggingConfiguredDestinationWriter Writer, Mock<IConfiguredDestinationWriter> Inner,
        List<DestinationActivityEntry> Entries) CreateWriter(
        DestinationWriteResult? result = null, Exception? throws = null)
    {
        var inner = new Mock<IConfiguredDestinationWriter>();
        var setup = inner.Setup(w => w.WriteAsync(
            It.IsAny<DestinationConfiguration>(),
            It.IsAny<MappingProfile>(),
            It.IsAny<IReadOnlyCollection<MappedDestinationRecord>>(),
            It.IsAny<PipelineWriteContext>(),
            It.IsAny<CancellationToken>()));

        if (throws is not null)
        {
            setup.ThrowsAsync(throws);
        }
        else
        {
            setup.ReturnsAsync(result ?? new DestinationWriteResult(1));
        }

        var entries = new List<DestinationActivityEntry>();
        var governance = new Mock<IGovernanceLogger>();
        governance
            .Setup(g => g.LogDestinationActivityAsync(It.IsAny<DestinationActivityEntry>(), It.IsAny<CancellationToken>()))
            .Callback<DestinationActivityEntry, CancellationToken>((entry, _) => entries.Add(entry))
            .Returns(Task.CompletedTask);

        return (
            new LoggingConfiguredDestinationWriter(inner.Object, NullLogger.Instance, governance.Object),
            inner,
            entries);
    }

    [Fact]
    public async Task A_successful_write_records_one_Succeeded_Complete_row_carrying_both_counts()
    {
        var (writer, _, entries) = CreateWriter(new DestinationWriteResult(1));

        await writer.WriteAsync(Destination(), Mapping(), [Record()], Context(), CancellationToken.None);

        var entry = entries.Should().ContainSingle().Subject;
        entry.Stage.Should().Be("Complete");
        entry.Status.Should().Be("Succeeded");
        entry.RecordCount.Should().Be(1);
        entry.WrittenCount.Should().Be(1);
        entry.CorrelationId.Should().Be("corr-1");
        entry.DestinationName.Should().Be("Warehouse");
    }

    [Fact]
    public async Task Rejected_records_are_recorded_as_PartialSuccess_with_the_first_reason()
    {
        // The write "succeeds" while silently dropping rows, so without this the trace would read as a clean run.
        var (writer, _, entries) = CreateWriter(
            new DestinationWriteResult(33, RecordErrors: ["Cannot insert NULL into column 'OnsetDateTime'"]));

        var records = Enumerable.Range(0, 50).Select(_ => Record()).ToArray();
        await writer.WriteAsync(Destination(), Mapping(), records, Context(), CancellationToken.None);

        var entry = entries.Should().ContainSingle().Subject;
        entry.Status.Should().Be("PartialSuccess");
        entry.RecordCount.Should().Be(50);
        entry.WrittenCount.Should().Be(33);
        entry.Error.Should().Contain("OnsetDateTime");
    }

    [Fact]
    public async Task An_empty_batch_is_NoData_rather_than_Succeeded()
    {
        var (writer, _, entries) = CreateWriter(new DestinationWriteResult(0));

        await writer.WriteAsync(Destination(), Mapping(), [], Context(), CancellationToken.None);

        entries.Should().ContainSingle().Which.Status.Should().Be("NoData");
    }

    [Fact]
    public async Task A_failed_write_records_the_reason_and_still_propagates_the_exception()
    {
        var (writer, _, entries) = CreateWriter(throws: new InvalidOperationException("Login failed for user 'svc'."));

        var act = async () => await writer.WriteAsync(
            Destination(), Mapping(), [Record()], Context(), CancellationToken.None);

        // Both halves matter: the row is what the screen shows, and the rethrow is what still fails the run.
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Login failed for user 'svc'.");

        var entry = entries.Should().ContainSingle().Subject;
        entry.Status.Should().Be("Failed");
        entry.Error.Should().Be("Login failed for user 'svc'.");
    }

    [Fact]
    public async Task A_governance_failure_never_fails_the_write()
    {
        // The whole point of the swallow contract: a schema drift or a database outage must not turn a
        // successful destination write into a failed one.
        var inner = new Mock<IConfiguredDestinationWriter>();
        inner.Setup(w => w.WriteAsync(
                It.IsAny<DestinationConfiguration>(), It.IsAny<MappingProfile>(),
                It.IsAny<IReadOnlyCollection<MappedDestinationRecord>>(), It.IsAny<PipelineWriteContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DestinationWriteResult(1));

        var governance = new Mock<IGovernanceLogger>();
        governance
            .Setup(g => g.LogDestinationActivityAsync(It.IsAny<DestinationActivityEntry>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("governance table is gone"));

        var writer = new LoggingConfiguredDestinationWriter(inner.Object, NullLogger.Instance, governance.Object);

        var result = await writer.WriteAsync(Destination(), Mapping(), [Record()], Context(), CancellationToken.None);

        result.Count.Should().Be(1);
    }

    [Fact]
    public async Task A_governance_failure_does_not_mask_the_writes_own_exception()
    {
        // In the catch branch the reporting call runs while an exception is already in flight; if it threw, it
        // would REPLACE the real failure and the caller would be told the wrong thing went wrong.
        var inner = new Mock<IConfiguredDestinationWriter>();
        inner.Setup(w => w.WriteAsync(
                It.IsAny<DestinationConfiguration>(), It.IsAny<MappingProfile>(),
                It.IsAny<IReadOnlyCollection<MappedDestinationRecord>>(), It.IsAny<PipelineWriteContext>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("the real failure"));

        var governance = new Mock<IGovernanceLogger>();
        governance
            .Setup(g => g.LogDestinationActivityAsync(It.IsAny<DestinationActivityEntry>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("governance table is gone"));

        var writer = new LoggingConfiguredDestinationWriter(inner.Object, NullLogger.Instance, governance.Object);

        var act = async () => await writer.WriteAsync(
            Destination(), Mapping(), [Record()], Context(), CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>().WithMessage("the real failure");
    }

    [Fact]
    public async Task Without_a_governance_logger_the_write_still_runs()
    {
        // The Worker's in-memory (no-database) configuration registers none.
        var inner = new Mock<IConfiguredDestinationWriter>();
        inner.Setup(w => w.WriteAsync(
                It.IsAny<DestinationConfiguration>(), It.IsAny<MappingProfile>(),
                It.IsAny<IReadOnlyCollection<MappedDestinationRecord>>(), It.IsAny<PipelineWriteContext>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DestinationWriteResult(1));

        var writer = new LoggingConfiguredDestinationWriter(inner.Object, NullLogger.Instance, governanceLogger: null);

        var result = await writer.WriteAsync(Destination(), Mapping(), [Record()], Context(), CancellationToken.None);

        result.Count.Should().Be(1);
    }

    [Fact]
    public async Task The_inner_writer_receives_a_stage_hook_it_can_report_a_connect_through()
    {
        var (writer, inner, entries) = CreateWriter();

        PipelineWriteContext? seen = null;
        inner.Setup(w => w.WriteAsync(
                It.IsAny<DestinationConfiguration>(), It.IsAny<MappingProfile>(),
                It.IsAny<IReadOnlyCollection<MappedDestinationRecord>>(), It.IsAny<PipelineWriteContext>(),
                It.IsAny<CancellationToken>()))
            .Callback<DestinationConfiguration, MappingProfile, IReadOnlyCollection<MappedDestinationRecord>,
                PipelineWriteContext, CancellationToken>((_, _, _, ctx, _) => seen = ctx)
            .ReturnsAsync(new DestinationWriteResult(1));

        await writer.WriteAsync(Destination(), Mapping(), [Record()], Context(), CancellationToken.None);

        seen!.ReportStageAsync.Should().NotBeNull();

        // What a writer reports lands in the same table, already stamped with the destination's identity.
        await seen.ReportStageAsync!(
            new DestinationStageReport("Connect", "Succeeded", 412, "Warehouse SQL"), CancellationToken.None);

        var connect = entries.Should().Contain(x => x.Stage == "Connect").Which;
        connect.Status.Should().Be("Succeeded");
        connect.Detail.Should().Be("Warehouse SQL");
        connect.DestinationName.Should().Be("Warehouse");
        connect.CorrelationId.Should().Be("corr-1");
    }

    [Fact]
    public async Task A_hook_the_caller_supplied_is_chained_rather_than_replaced()
    {
        var (writer, inner, _) = CreateWriter();

        var callerSawStages = new List<string>();
        var context = Context() with
        {
            ReportStageAsync = (report, _) =>
            {
                callerSawStages.Add(report.Stage);
                return Task.CompletedTask;
            },
        };

        PipelineWriteContext? seen = null;
        inner.Setup(w => w.WriteAsync(
                It.IsAny<DestinationConfiguration>(), It.IsAny<MappingProfile>(),
                It.IsAny<IReadOnlyCollection<MappedDestinationRecord>>(), It.IsAny<PipelineWriteContext>(),
                It.IsAny<CancellationToken>()))
            .Callback<DestinationConfiguration, MappingProfile, IReadOnlyCollection<MappedDestinationRecord>,
                PipelineWriteContext, CancellationToken>((_, _, _, ctx, _) => seen = ctx)
            .ReturnsAsync(new DestinationWriteResult(1));

        await writer.WriteAsync(Destination(), Mapping(), [Record()], context, CancellationToken.None);
        await seen!.ReportStageAsync!(
            new DestinationStageReport("Connect", "Succeeded"), CancellationToken.None);

        callerSawStages.Should().ContainSingle().Which.Should().Be("Connect");
    }
}
