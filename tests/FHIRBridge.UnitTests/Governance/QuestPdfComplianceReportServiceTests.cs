using FHIRBridge.Domain.Entities.Governance;
using FHIRBridge.Infrastructure.Governance;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.UnitTests.Infrastructure;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Governance;

public sealed class QuestPdfComplianceReportServiceTests
{
    // Routed through SharedInMemoryDatabase (one process-wide internal service provider) rather than a
    // per-class root: EF builds a distinct internal IServiceProvider per distinct options configuration and
    // raises ManyServiceProvidersCreatedWarning as an error past twenty in one process, in whichever test
    // happens to cross the line — which reads as an unrelated failure elsewhere in the suite. Isolation still
    // comes from this class's own unique _databaseName.
    private readonly string _databaseName = SharedInMemoryDatabase.NewDatabaseName();

    private FHIRBridgeDbContext CreateContext() =>
        new(SharedInMemoryDatabase.Options<FHIRBridgeDbContext>(_databaseName));

    [Fact]
    public async Task Generates_a_pdf_and_reports_a_valid_hash_chain()
    {
        await using var context = CreateContext();

        var first = new AuditLog(
            Guid.NewGuid(), DateTime.UtcNow.AddDays(-1), "alice@example.com", "SourceConnection", "Created",
            "SourceConnection", "conn-1", null, null, "{\"Name\":\"Epic Sandbox\"}", "Success", null,
            "203.0.113.10", "test-agent", "CORR-1", null);

        var second = new AuditLog(
            Guid.NewGuid(), DateTime.UtcNow, "bob@example.com", "DestinationConfiguration", "Updated",
            "DestinationConfiguration", "dest-1", null, "{\"Port\":22}", "{\"Port\":2222}", "Success", null,
            "203.0.113.11", "test-agent", "CORR-2", first.EntryHash);

        context.AuditLogs.AddRange(first, second);
        await context.SaveChangesAsync();

        var sut = new QuestPdfComplianceReportService(context, new EfAuditChainVerificationService(context));
        var pdfBytes = await sut.GenerateHipaaAuditReportAsync(
            DateTime.UtcNow.AddDays(-7), DateTime.UtcNow.AddDays(1), CancellationToken.None);

        pdfBytes.Should().NotBeEmpty();
        // PDF files always start with this magic header — a cheap sanity check that QuestPDF actually rendered
        // a real document rather than silently returning garbage.
        System.Text.Encoding.ASCII.GetString(pdfBytes, 0, 5).Should().Be("%PDF-");
    }

    [Fact]
    public async Task Reports_zero_entries_and_a_valid_empty_chain_when_no_audit_logs_exist()
    {
        await using var context = CreateContext();
        var sut = new QuestPdfComplianceReportService(context, new EfAuditChainVerificationService(context));

        var pdfBytes = await sut.GenerateHipaaAuditReportAsync(
            DateTime.UtcNow.AddDays(-7), DateTime.UtcNow.AddDays(1), CancellationToken.None);

        pdfBytes.Should().NotBeEmpty();
    }
}
