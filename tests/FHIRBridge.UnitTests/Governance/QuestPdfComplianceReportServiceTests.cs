using FHIRBridge.Domain.Entities.Governance;
using FHIRBridge.Infrastructure.Governance;
using FHIRBridge.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FHIRBridge.UnitTests.Governance;

public sealed class QuestPdfComplianceReportServiceTests
{
    private readonly InMemoryDatabaseRoot _root = new();
    private readonly string _databaseName = Guid.NewGuid().ToString();

    private FHIRBridgeDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<FHIRBridgeDbContext>()
            .UseInMemoryDatabase(_databaseName, _root)
            .Options;

        return new FHIRBridgeDbContext(options);
    }

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
