using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Domain.Entities.Governance;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Renders the HIPAA/SOC2 compliance report as a PDF: audit-trail hash-chain integrity (via
/// <see cref="IAuditChainVerificationService"/>, shared with the scheduled <c>AuditChainVerificationWorker</c>
/// so the walk logic lives in one place), plus authentication, data-access, security-event, and error activity
/// for a given period.
/// </summary>
public sealed class QuestPdfComplianceReportService : IComplianceReportService
{
    static QuestPdfComplianceReportService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    private readonly FHIRBridgeDbContext _dbContext;
    private readonly IAuditChainVerificationService _chainVerificationService;

    public QuestPdfComplianceReportService(
        FHIRBridgeDbContext dbContext, IAuditChainVerificationService chainVerificationService)
    {
        _dbContext = dbContext;
        _chainVerificationService = chainVerificationService;
    }

    public async Task<byte[]> GenerateHipaaAuditReportAsync(
        DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        var chainResult = await _chainVerificationService.VerifyAsync(cancellationToken);

        var auditModuleCounts = await _dbContext.AuditLogs.AsNoTracking()
            .Where(x => x.OccurredOnUtc >= fromUtc && x.OccurredOnUtc <= toUtc)
            .GroupBy(x => x.Module)
            .Select(g => new { Module = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(15)
            .ToListAsync(cancellationToken);
        var auditEntriesInPeriod = auditModuleCounts.Sum(x => x.Count);

        var authInPeriod = await _dbContext.AuthenticationLogs.AsNoTracking()
            .Where(x => x.OccurredOnUtc >= fromUtc && x.OccurredOnUtc <= toUtc)
            .ToListAsync(cancellationToken);
        var authTotal = authInPeriod.Count;
        var authSuccess = authInPeriod.Count(x => x.Success);
        var authFailed = authTotal - authSuccess;
        var authDistinctUsers = authInPeriod.Select(x => x.UserEmail).Where(e => e is not null).Distinct().Count();

        var dataAccessInPeriod = await _dbContext.DataAccessLogs.AsNoTracking()
            .Where(x => x.OccurredOnUtc >= fromUtc && x.OccurredOnUtc <= toUtc)
            .GroupBy(x => x.Action)
            .Select(g => new { Action = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var dataAccessTotal = dataAccessInPeriod.Sum(x => x.Count);

        var securityEventsInPeriod = await _dbContext.SecurityEvents.AsNoTracking()
            .Where(x => x.OccurredOnUtc >= fromUtc && x.OccurredOnUtc <= toUtc)
            .ToListAsync(cancellationToken);
        var securityTotal = securityEventsInPeriod.Count;
        var securityUnresolved = securityEventsInPeriod.Count(x => !x.Resolved);
        var securityBySeverity = securityEventsInPeriod
            .GroupBy(x => x.Severity)
            .Select(g => new { Severity = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToList();

        var errorsInPeriod = await _dbContext.ErrorLogs.AsNoTracking()
            .Where(x => x.OccurredOnUtc >= fromUtc && x.OccurredOnUtc <= toUtc)
            .GroupBy(x => x.Severity)
            .Select(g => new { Severity = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var errorsTotal = errorsInPeriod.Sum(x => x.Count);

        var generatedOnUtc = DateTime.UtcNow;

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().Column(col =>
                {
                    col.Item().Text("Segue Compliance Report").FontSize(18).Bold();
                    col.Item().Text("HIPAA §164.312(b) Audit Controls & SOC 2 Evidence").FontSize(11);
                    col.Item().PaddingTop(4).Text($"Period: {fromUtc:yyyy-MM-dd} to {toUtc:yyyy-MM-dd} (UTC)  |  Generated: {generatedOnUtc:yyyy-MM-dd HH:mm} UTC");
                });

                page.Content().PaddingTop(10).Column(col =>
                {
                    col.Spacing(14);

                    col.Item().Element(c => SectionHeader(c, "Audit Trail Integrity"));
                    col.Item().Text($"Hash-chain verification: {(chainResult.IsValid ? "VERIFIED" : "BROKEN")}").Bold();
                    col.Item().Text($"Total audit log entries (all-time): {chainResult.TotalEntries}");
                    col.Item().Text($"Entries in reporting period: {auditEntriesInPeriod}");
                    if (!chainResult.IsValid)
                    {
                        col.Item().Text($"First inconsistency at SequenceNumber {chainResult.FirstBrokenSequenceNumber}.").FontColor(Colors.Red.Medium);
                    }
                    if (auditModuleCounts.Count > 0)
                    {
                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(d => { d.RelativeColumn(3); d.RelativeColumn(1); });
                            table.Cell().Background(Colors.Grey.Lighten2).Padding(3).Text("Module").Bold();
                            table.Cell().Background(Colors.Grey.Lighten2).Padding(3).Text("Changes").Bold();
                            foreach (var row in auditModuleCounts)
                            {
                                table.Cell().Padding(3).Text(row.Module);
                                table.Cell().Padding(3).Text(row.Count.ToString());
                            }
                        });
                    }

                    col.Item().Element(c => SectionHeader(c, "Authentication"));
                    col.Item().Text($"Total attempts: {authTotal}  |  Successful: {authSuccess}  |  Failed: {authFailed}  |  Distinct users: {authDistinctUsers}");

                    col.Item().Element(c => SectionHeader(c, "Patient / Resource Data Access"));
                    col.Item().Text($"Total access events: {dataAccessTotal}");
                    foreach (var row in dataAccessInPeriod)
                    {
                        col.Item().Text($"  {row.Action}: {row.Count}");
                    }

                    col.Item().Element(c => SectionHeader(c, "Security Events"));
                    col.Item().Text($"Total: {securityTotal}  |  Unresolved: {securityUnresolved}");
                    foreach (var row in securityBySeverity)
                    {
                        col.Item().Text($"  {row.Severity}: {row.Count}");
                    }

                    col.Item().Element(c => SectionHeader(c, "Errors"));
                    col.Item().Text($"Total: {errorsTotal}");
                    foreach (var row in errorsInPeriod)
                    {
                        col.Item().Text($"  {row.Severity}: {row.Count}");
                    }
                });

                page.Footer().AlignCenter().Text(x =>
                {
                    x.Span("Page ");
                    x.CurrentPageNumber();
                    x.Span(" of ");
                    x.TotalPages();
                });
            });
        });

        return document.GeneratePdf();
    }

    public async Task<byte[]> GenerateSoc2EvidenceReportAsync(
        DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        var chainResult = await _chainVerificationService.VerifyAsync(cancellationToken);

        var changeManagementCounts = await _dbContext.AuditLogs.AsNoTracking()
            .Where(x => x.OccurredOnUtc >= fromUtc && x.OccurredOnUtc <= toUtc)
            .GroupBy(x => x.Action)
            .Select(g => new { Action = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var changeManagementTotal = changeManagementCounts.Sum(x => x.Count);

        var authorizationDenials = await _dbContext.AuthorizationLogs.AsNoTracking()
            .Where(x => x.OccurredOnUtc >= fromUtc && x.OccurredOnUtc <= toUtc && x.Result == "Denied")
            .GroupBy(x => x.PermissionCode)
            .Select(g => new { PermissionCode = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(15)
            .ToListAsync(cancellationToken);
        var authorizationDenialsTotal = authorizationDenials.Sum(x => x.Count);

        var endpointChecksInPeriod = await _dbContext.EndpointHealthChecks.AsNoTracking()
            .Where(x => x.OccurredOnUtc >= fromUtc && x.OccurredOnUtc <= toUtc)
            .ToListAsync(cancellationToken);
        var availabilityByEndpoint = endpointChecksInPeriod
            .GroupBy(x => x.EndpointName)
            .Select(g => new { EndpointName = g.Key, Total = g.Count(), Healthy = g.Count(x => x.Status == "Healthy") })
            .OrderBy(x => x.EndpointName)
            .ToList();

        var securityEventsInPeriod = await _dbContext.SecurityEvents.AsNoTracking()
            .Where(x => x.OccurredOnUtc >= fromUtc && x.OccurredOnUtc <= toUtc)
            .GroupBy(x => x.Severity)
            .Select(g => new { Severity = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync(cancellationToken);
        var securityEventsTotal = securityEventsInPeriod.Sum(x => x.Count);

        var generatedOnUtc = DateTime.UtcNow;

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().Column(col =>
                {
                    col.Item().Text("Segue SOC 2 Evidence Export").FontSize(18).Bold();
                    col.Item().Text("Trust Services Criteria: Security, Availability, Processing Integrity").FontSize(11);
                    col.Item().PaddingTop(4).Text($"Period: {fromUtc:yyyy-MM-dd} to {toUtc:yyyy-MM-dd} (UTC)  |  Generated: {generatedOnUtc:yyyy-MM-dd HH:mm} UTC");
                    col.Item().PaddingTop(2).Text("Manual-trigger export — no automated monthly schedule exists yet.").FontSize(8).Italic();
                });

                page.Content().PaddingTop(10).Column(col =>
                {
                    col.Spacing(14);

                    col.Item().Element(c => SectionHeader(c, "Processing Integrity — Audit Trail"));
                    col.Item().Text($"Hash-chain verification: {(chainResult.IsValid ? "VERIFIED" : "BROKEN")}").Bold();
                    col.Item().Text($"Total audit log entries (all-time): {chainResult.TotalEntries}");
                    if (!chainResult.IsValid)
                    {
                        col.Item().Text($"First inconsistency at SequenceNumber {chainResult.FirstBrokenSequenceNumber}.").FontColor(Colors.Red.Medium);
                    }

                    col.Item().Element(c => SectionHeader(c, "Change Management"));
                    col.Item().Text($"Total configuration changes in period: {changeManagementTotal}");
                    foreach (var row in changeManagementCounts)
                    {
                        col.Item().Text($"  {row.Action}: {row.Count}");
                    }

                    col.Item().Element(c => SectionHeader(c, "Access Control — Authorization Denials"));
                    col.Item().Text($"Total denials in period: {authorizationDenialsTotal}");
                    foreach (var row in authorizationDenials)
                    {
                        col.Item().Text($"  {row.PermissionCode}: {row.Count}");
                    }

                    col.Item().Element(c => SectionHeader(c, "Availability — Endpoint Health Checks"));
                    if (availabilityByEndpoint.Count == 0)
                    {
                        col.Item().Text("No endpoint health checks recorded in this period.");
                    }
                    foreach (var row in availabilityByEndpoint)
                    {
                        var uptimePercent = row.Total == 0 ? 0 : row.Healthy * 100.0 / row.Total;
                        col.Item().Text($"  {row.EndpointName}: {uptimePercent:F1}% uptime ({row.Healthy}/{row.Total} checks)");
                    }

                    col.Item().Element(c => SectionHeader(c, "Security Incidents"));
                    col.Item().Text($"Total: {securityEventsTotal}");
                    foreach (var row in securityEventsInPeriod)
                    {
                        col.Item().Text($"  {row.Severity}: {row.Count}");
                    }
                });

                page.Footer().AlignCenter().Text(x =>
                {
                    x.Span("Page ");
                    x.CurrentPageNumber();
                    x.Span(" of ");
                    x.TotalPages();
                });
            });
        });

        return document.GeneratePdf();
    }

    private static void SectionHeader(QuestPDF.Infrastructure.IContainer container, string title)
    {
        container.PaddingBottom(2).BorderBottom(1).BorderColor(Colors.Grey.Lighten1)
            .Text(title).FontSize(13).Bold();
    }
}
