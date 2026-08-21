namespace FHIRBridge.Application.Abstractions.Governance;

/// <summary>Generates the auditor-facing compliance report — the concrete artifact a HIPAA/SOC2 review asks for.</summary>
public interface IComplianceReportService
{
    /// <summary>Renders a PDF summarizing audit-trail integrity, authentication, data access, security, and
    /// error activity for the given UTC date range.</summary>
    Task<byte[]> GenerateHipaaAuditReportAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Renders a distinct SOC2-framed PDF (access reviews/change management from AuditLogs, availability/incident
    /// evidence from SecurityEvents and EndpointHealthChecks) for the given UTC date range. Manual-trigger-only in
    /// this pass — no scheduled monthly generation exists; the report itself says so rather than implying one.
    /// </summary>
    Task<byte[]> GenerateSoc2EvidenceReportAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken);
}
