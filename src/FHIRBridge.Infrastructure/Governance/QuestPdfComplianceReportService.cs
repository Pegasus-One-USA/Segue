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
    private static readonly Color BrandTeal = Color.FromHex("#00A89D");
    private static readonly Color BrandTealDark = Color.FromHex("#00786F");
    private static readonly Color AlertRed = Color.FromHex("#C0392B");
    private static readonly Color AlertAmber = Color.FromHex("#B8860B");
    private static readonly Color RowStripe = Color.FromHex("#F4FBFA");

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
        var authSuccessRate = authTotal == 0 ? (double?)null : authSuccess * 100.0 / authTotal;

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
        var reportId = $"HIPAA-{fromUtc:yyyyMMdd}-{toUtc:yyyyMMdd}-{generatedOnUtc:HHmmss}";

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().Element(c => RenderHeader(
                    c, "Segue Compliance Report", "HIPAA §164.312(b) Audit Controls & SOC 2 Evidence",
                    fromUtc, toUtc, generatedOnUtc, reportId, note: null));

                page.Content().PaddingTop(12).Column(col =>
                {
                    col.Spacing(16);

                    col.Item().Element(c => RenderMetricsSummary(c, new (string, string, bool)[]
                    {
                        ("Audit Chain", chainResult.IsValid ? "Verified" : "BROKEN", !chainResult.IsValid),
                        ("Audit Entries (period)", auditEntriesInPeriod.ToString(), false),
                        ("Login Success Rate", authSuccessRate is { } rate ? $"{rate:F0}%" : "N/A", authSuccessRate is { } r2 && r2 < 90),
                        ("Unresolved Security Events", securityUnresolved.ToString(), securityUnresolved > 0),
                        ("Errors (period)", errorsTotal.ToString(), errorsTotal > 0),
                    }));

                    col.Item().Element(c => SectionHeader(c, "Audit Trail Integrity"));
                    col.Item().Text($"Total audit log entries (all-time): {chainResult.TotalEntries}");
                    if (!chainResult.IsValid)
                    {
                        col.Item().Text($"First inconsistency at SequenceNumber {chainResult.FirstBrokenSequenceNumber}.")
                            .FontColor(AlertRed).Bold();
                    }
                    if (auditModuleCounts.Count > 0)
                    {
                        col.Item().Element(c => RenderTable(
                            c,
                            ["Module", "Configuration Changes"],
                            auditModuleCounts.Select(row => new[] { row.Module, row.Count.ToString() }),
                            [3, 1]));
                    }

                    col.Item().Element(c => SectionHeader(c, "Authentication"));
                    col.Item().Element(c => RenderTable(
                        c,
                        ["Total Attempts", "Successful", "Failed", "Distinct Users"],
                        [[authTotal.ToString(), authSuccess.ToString(), authFailed.ToString(), authDistinctUsers.ToString()]]));

                    col.Item().Element(c => SectionHeader(c, "Patient / Resource Data Access"));
                    col.Item().Text($"Total access events: {dataAccessTotal}");
                    if (dataAccessInPeriod.Count > 0)
                    {
                        col.Item().Element(c => RenderTable(
                            c,
                            ["Action", "Count"],
                            dataAccessInPeriod.Select(row => new[] { row.Action, row.Count.ToString() }),
                            [3, 1]));
                    }

                    col.Item().Element(c => SectionHeader(c, "Security Events"));
                    col.Item().Text($"Total: {securityTotal}  |  Unresolved: {securityUnresolved}");
                    if (securityBySeverity.Count > 0)
                    {
                        col.Item().Element(c => RenderSeverityTable(c, securityBySeverity.Select(row => (row.Severity, row.Count))));
                    }

                    col.Item().Element(c => SectionHeader(c, "Errors"));
                    col.Item().Text($"Total: {errorsTotal}");
                    if (errorsInPeriod.Count > 0)
                    {
                        col.Item().Element(c => RenderSeverityTable(c, errorsInPeriod.Select(row => (row.Severity, row.Count))));
                    }

                    col.Item().Element(c => RenderGlossary(c, HipaaGlossary));
                });

                page.Footer().Element(c => RenderFooter(c, reportId));
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
        var overallUptimePercent = endpointChecksInPeriod.Count == 0
            ? (double?)null
            : endpointChecksInPeriod.Count(x => x.Status == "Healthy") * 100.0 / endpointChecksInPeriod.Count;

        var securityEventsInPeriod = await _dbContext.SecurityEvents.AsNoTracking()
            .Where(x => x.OccurredOnUtc >= fromUtc && x.OccurredOnUtc <= toUtc)
            .GroupBy(x => x.Severity)
            .Select(g => new { Severity = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync(cancellationToken);
        var securityEventsTotal = securityEventsInPeriod.Sum(x => x.Count);

        var generatedOnUtc = DateTime.UtcNow;
        var reportId = $"SOC2-{fromUtc:yyyyMMdd}-{toUtc:yyyyMMdd}-{generatedOnUtc:HHmmss}";

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().Element(c => RenderHeader(
                    c, "Segue SOC 2 Evidence Export", "Trust Services Criteria: Security, Availability, Processing Integrity",
                    fromUtc, toUtc, generatedOnUtc, reportId,
                    note: "Manual-trigger export — no automated monthly schedule exists yet."));

                page.Content().PaddingTop(12).Column(col =>
                {
                    col.Spacing(16);

                    col.Item().Element(c => RenderMetricsSummary(c, new (string, string, bool)[]
                    {
                        ("Audit Chain", chainResult.IsValid ? "Verified" : "BROKEN", !chainResult.IsValid),
                        ("Config Changes (period)", changeManagementTotal.ToString(), false),
                        ("Authorization Denials", authorizationDenialsTotal.ToString(), authorizationDenialsTotal > 0),
                        ("Overall Uptime", overallUptimePercent is { } up ? $"{up:F1}%" : "N/A", overallUptimePercent is { } u2 && u2 < 99),
                        ("Security Incidents", securityEventsTotal.ToString(), securityEventsTotal > 0),
                    }));

                    col.Item().Element(c => SectionHeader(c, "Processing Integrity — Audit Trail"));
                    col.Item().Text($"Total audit log entries (all-time): {chainResult.TotalEntries}");
                    if (!chainResult.IsValid)
                    {
                        col.Item().Text($"First inconsistency at SequenceNumber {chainResult.FirstBrokenSequenceNumber}.")
                            .FontColor(AlertRed).Bold();
                    }

                    col.Item().Element(c => SectionHeader(c, "Change Management"));
                    col.Item().Text($"Total configuration changes in period: {changeManagementTotal}");
                    if (changeManagementCounts.Count > 0)
                    {
                        col.Item().Element(c => RenderTable(
                            c,
                            ["Action", "Count"],
                            changeManagementCounts.Select(row => new[] { row.Action, row.Count.ToString() }),
                            [3, 1]));
                    }

                    col.Item().Element(c => SectionHeader(c, "Access Control — Authorization Denials"));
                    col.Item().Text($"Total denials in period: {authorizationDenialsTotal}");
                    if (authorizationDenials.Count > 0)
                    {
                        col.Item().Element(c => RenderTable(
                            c,
                            ["Permission Code", "Denials"],
                            authorizationDenials.Select(row => new[] { row.PermissionCode, row.Count.ToString() }),
                            [3, 1]));
                    }

                    col.Item().Element(c => SectionHeader(c, "Availability — Endpoint Health Checks"));
                    if (availabilityByEndpoint.Count == 0)
                    {
                        col.Item().Text("No endpoint health checks recorded in this period.");
                    }
                    else
                    {
                        col.Item().Element(c => RenderTable(
                            c,
                            ["Endpoint", "Uptime", "Checks"],
                            availabilityByEndpoint.Select(row =>
                            {
                                var uptimePercent = row.Total == 0 ? 0 : row.Healthy * 100.0 / row.Total;
                                return new[] { row.EndpointName, $"{uptimePercent:F1}%", $"{row.Healthy}/{row.Total}" };
                            }),
                            [2, 1, 1]));
                    }

                    col.Item().Element(c => SectionHeader(c, "Security Incidents"));
                    col.Item().Text($"Total: {securityEventsTotal}");
                    if (securityEventsInPeriod.Count > 0)
                    {
                        col.Item().Element(c => RenderSeverityTable(c, securityEventsInPeriod.Select(row => (row.Severity, row.Count))));
                    }

                    col.Item().Element(c => RenderGlossary(c, Soc2Glossary));
                });

                page.Footer().Element(c => RenderFooter(c, reportId));
            });
        });

        return document.GeneratePdf();
    }

    // ── Shared rendering helpers ──────────────────────────────────────────────

    private static void RenderHeader(
        IContainer container, string title, string subtitle,
        DateTime fromUtc, DateTime toUtc, DateTime generatedOnUtc, string reportId, string? note)
    {
        container.Background(BrandTealDark).Padding(16).Column(col =>
        {
            col.Item().Text(title).FontSize(18).Bold().FontColor(Colors.White);
            col.Item().PaddingTop(2).Text(subtitle).FontSize(11).FontColor(Colors.White);
            col.Item().PaddingTop(6).Text(
                $"Period: {fromUtc:yyyy-MM-dd} to {toUtc:yyyy-MM-dd} (UTC)   |   Generated: {generatedOnUtc:yyyy-MM-dd HH:mm} UTC   |   Report ID: {reportId}")
                .FontSize(8).FontColor(Colors.White);
            if (note is not null)
            {
                col.Item().PaddingTop(2).Text(note).FontSize(8).Italic().FontColor(Colors.White);
            }
        });
    }

    private static void RenderFooter(IContainer container, string reportId)
    {
        container.PaddingTop(6).BorderTop(1).BorderColor(Colors.Grey.Lighten2).Row(row =>
        {
            row.RelativeItem().Text("CONFIDENTIAL — HIPAA/SOC 2 Compliance Evidence. Contains no PHI.")
                .FontSize(7).FontColor(Colors.Grey.Darken1);
            row.RelativeItem().AlignRight().Text(x =>
            {
                x.DefaultTextStyle(t => t.FontSize(7).FontColor(Colors.Grey.Darken1));
                x.Span($"{reportId}   |   Page ");
                x.CurrentPageNumber();
                x.Span(" of ");
                x.TotalPages();
            });
        });
    }

    private static void SectionHeader(IContainer container, string title)
    {
        container.PaddingBottom(2).BorderBottom(2).BorderColor(BrandTeal)
            .Text(title).FontSize(13).Bold().FontColor(BrandTealDark);
    }

    /// <summary>At-a-glance scorecard: each metric is its own bordered tile, colored red when flagged as an
    /// alert condition (broken chain, unresolved incidents, elevated error/failure rates) so the single most
    /// important signal in the report — is anything wrong — doesn't require reading every section to find.</summary>
    private static void RenderMetricsSummary(IContainer container, IReadOnlyList<(string Label, string Value, bool Alert)> metrics)
    {
        container.Row(row =>
        {
            foreach (var metric in metrics)
            {
                row.RelativeItem().Padding(2).Border(1)
                    .BorderColor(metric.Alert ? AlertRed : Colors.Grey.Lighten2)
                    .Background(metric.Alert ? Color.FromHex("#FDECEA") : RowStripe)
                    .Padding(8).Column(col =>
                    {
                        col.Item().Text(metric.Label).FontSize(7).FontColor(Colors.Grey.Darken2);
                        col.Item().PaddingTop(2).Text(metric.Value).FontSize(14).Bold()
                            .FontColor(metric.Alert ? AlertRed : BrandTealDark);
                    });
            }
        });
    }

    /// <summary>Generic shaded-header, zebra-striped table — the same visual treatment for every section
    /// instead of some sections being tables and others plain indented text lines.</summary>
    private static void RenderTable(IContainer container, string[] headers, IEnumerable<string[]> rows, int[]? relativeWidths = null)
    {
        var rowList = rows.ToList();
        container.Table(table =>
        {
            table.ColumnsDefinition(d =>
            {
                for (var i = 0; i < headers.Length; i++)
                {
                    d.RelativeColumn(relativeWidths is not null && i < relativeWidths.Length ? relativeWidths[i] : 1);
                }
            });

            foreach (var header in headers)
            {
                table.Cell().Background(BrandTeal).Padding(4).Text(header).Bold().FontColor(Colors.White).FontSize(9);
            }

            for (var i = 0; i < rowList.Count; i++)
            {
                var background = i % 2 == 0 ? Colors.White : RowStripe;
                foreach (var cellText in rowList[i])
                {
                    table.Cell().Background(background).Padding(4).Text(cellText).FontSize(9);
                }
            }
        });
    }

    /// <summary>Same table treatment as <see cref="RenderTable"/>, but the Severity column's text is
    /// colored by severity level so Critical/High rows are visually distinct from Low/Medium at a glance.</summary>
    private static void RenderSeverityTable(IContainer container, IEnumerable<(string Severity, int Count)> rows)
    {
        var rowList = rows.ToList();
        container.Table(table =>
        {
            table.ColumnsDefinition(d => { d.RelativeColumn(3); d.RelativeColumn(1); });

            table.Cell().Background(BrandTeal).Padding(4).Text("Severity").Bold().FontColor(Colors.White).FontSize(9);
            table.Cell().Background(BrandTeal).Padding(4).Text("Count").Bold().FontColor(Colors.White).FontSize(9);

            for (var i = 0; i < rowList.Count; i++)
            {
                var (severity, count) = rowList[i];
                var background = i % 2 == 0 ? Colors.White : RowStripe;
                var severityColor = severity switch
                {
                    "Critical" => AlertRed,
                    "High" => AlertRed,
                    "Medium" or "Warning" => AlertAmber,
                    _ => Colors.Black,
                };

                table.Cell().Background(background).Padding(4).Text(severity).FontSize(9).FontColor(severityColor).Bold();
                table.Cell().Background(background).Padding(4).Text(count.ToString()).FontSize(9);
            }
        });
    }

    private static void RenderGlossary(IContainer container, (string Term, string Definition)[] items)
    {
        container.Background(RowStripe).Padding(10).Column(col =>
        {
            col.Item().Text("Glossary").FontSize(9).Bold().FontColor(BrandTealDark);
            foreach (var (term, definition) in items)
            {
                col.Item().PaddingTop(3).Text(x =>
                {
                    x.DefaultTextStyle(t => t.FontSize(8).FontColor(Colors.Grey.Darken2));
                    x.Span($"{term}: ").Bold();
                    x.Span(definition);
                });
            }
        });
    }

    private static readonly (string Term, string Definition)[] HipaaGlossary =
    [
        ("Hash-chain verification", "Each audit log entry cryptographically references the previous one; \"Verified\" means no entry has been altered or deleted outside the application since the chain began."),
        ("Allowed / Denied", "The governance policy's access decision for a given resource access attempt, recorded regardless of outcome (HIPAA §164.312(b))."),
        ("Revealed", "A specific encrypted field value was decrypted and displayed to an administrator via the Data Lineage screen — a separate, permission-gated action from ordinary resource access, individually audited."),
        ("Distinct users", "The count of unique user accounts that attempted authentication in this period, not the count of attempts."),
    ];

    private static readonly (string Term, string Definition)[] Soc2Glossary =
    [
        ("Hash-chain verification", "Each audit log entry cryptographically references the previous one; \"Verified\" means no entry has been altered or deleted outside the application since the chain began."),
        ("Change management", "Configuration entity changes (users, roles, permissions, connections, mappings) captured automatically on every save — not manually logged."),
        ("Authorization denials", "Requests rejected by a role-based permission check (HTTP 403), grouped by the specific permission code that was missing."),
        ("Uptime", "Percentage of scheduled connectivity checks against a source/destination endpoint that reported \"Healthy\" in this period."),
    ];
}
