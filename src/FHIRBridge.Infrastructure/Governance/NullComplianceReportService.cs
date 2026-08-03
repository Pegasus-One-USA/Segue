using FHIRBridge.Application.Abstractions.Governance;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>In-memory (no-database) dev configuration: there's no governance data to report on.</summary>
public sealed class NullComplianceReportService : IComplianceReportService
{
    static NullComplianceReportService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public Task<byte[]> GenerateHipaaAuditReportAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.Content().Text(
                    "No database is configured for this environment, so there is no governance data to report on.");
            });
        });

        return Task.FromResult(document.GeneratePdf());
    }

    public Task<byte[]> GenerateSoc2EvidenceReportAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
        => GenerateHipaaAuditReportAsync(fromUtc, toUtc, cancellationToken);
}
