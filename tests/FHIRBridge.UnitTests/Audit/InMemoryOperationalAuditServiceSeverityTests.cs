using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Infrastructure.Audit;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Audit;

// P2: OperationalAuditLog gained a Severity field so the Operational Log can be filtered by INFO/WARNING/ERROR —
// covers the default (existing/unspecified-severity callers keep working unchanged) and the new filter.
public sealed class InMemoryOperationalAuditServiceSeverityTests
{
    [Fact]
    public async Task RecordAsync_defaults_severity_to_information_when_not_specified()
    {
        var service = new InMemoryOperationalAuditService();

        await service.RecordAsync(
            new RecordOperationalAuditLogRequest(null, null, null, null, null, null, "Action", "Completed", "msg", null, null, null),
            CancellationToken.None);

        var recent = await service.GetRecentAsync(10, CancellationToken.None);
        recent.Should().ContainSingle(x => x.Severity == OperationalLogSeverities.Information);
    }

    [Fact]
    public async Task GetPagedAsync_filters_by_severity()
    {
        var service = new InMemoryOperationalAuditService();
        await service.RecordAsync(
            new RecordOperationalAuditLogRequest(null, null, null, null, null, null, "A", "Completed", "info entry", null, null, null,
                OperationalLogSeverities.Information),
            CancellationToken.None);
        await service.RecordAsync(
            new RecordOperationalAuditLogRequest(null, null, null, null, null, null, "B", "Retrying", "warning entry", null, null, null,
                OperationalLogSeverities.Warning),
            CancellationToken.None);

        var result = await service.GetPagedAsync(
            new OperationalAuditLogFilter(null, null, null, null, null, OperationalLogSeverities.Warning),
            page: 1, pageSize: 25, CancellationToken.None);

        result.Items.Should().ContainSingle(x => x.Message == "warning entry");
    }
}
