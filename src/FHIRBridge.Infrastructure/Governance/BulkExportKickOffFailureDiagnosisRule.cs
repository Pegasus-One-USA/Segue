using System.Text.RegularExpressions;
using FHIRBridge.Governance;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Diagnoses FHIR Bulk Data Export kick-off failures (see <c>FhirRestBulkExportClient.KickOffAsync</c>, which
/// embeds the HTTP status and a raw response-body snippet in the thrown <see cref="InvalidOperationException"/>'s
/// message). Matches by message shape rather than a direct dependency on the Runtime.Infrastructure connector that
/// throws it — same approach as <see cref="TokenEndpointFailureDiagnosisRule"/>.
/// </summary>
public sealed partial class BulkExportKickOffFailureDiagnosisRule : IFailureDiagnosisRule
{
    public bool Matches(Exception exception) =>
        exception.Message.Contains("Bulk export kick-off returned", StringComparison.OrdinalIgnoreCase);

    public Diagnosis Diagnose(Exception exception)
    {
        var statusMatch = StatusCodeRegex().Match(exception.Message);
        var statusCode = statusMatch.Success ? statusMatch.Groups[1].Value : null;

        return statusCode switch
        {
            "404" => new Diagnosis(
                "This Epic connection's configured export scope or resource type isn't supported at that endpoint " +
                "— Epic sandboxes typically only support Group-level bulk export, not System-level. Check the " +
                "Export Scope setting on this source connection (try Group with a valid Group ID, or Patient), or " +
                "confirm with your Epic App Orchard registration which export scope is enabled for this client.",
                DiagnosisAction.SelfFix),
            "401" or "403" => new Diagnosis(
                "One or more of the resource types configured for this export aren't authorized for this Epic " +
                "client's registered scopes. Check the Epic App Orchard registration for this client ID and " +
                "confirm the resource type(s) configured here are included in its granted scopes.",
                DiagnosisAction.SelfFix),
            _ => new Diagnosis(
                "The Epic FHIR server rejected the bulk export request. Check the Export Scope, resource types, " +
                "and Group ID (if applicable) configured on this source connection.",
                DiagnosisAction.SelfFix),
        };
    }

    [GeneratedRegex(@"returned (\d{3})")]
    private static partial Regex StatusCodeRegex();
}
