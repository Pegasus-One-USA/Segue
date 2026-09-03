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
        exception.Message.Contains("Bulk export kick-off returned", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("Bulk export kick-off rejected as a duplicate", StringComparison.OrdinalIgnoreCase);

    public Diagnosis Diagnose(Exception exception)
    {
        // A duplicate rejection carries no HTTP status in the message (some servers, e.g. eCW, signal it as a 200 with
        // an OperationOutcome), so match it by phrase before falling through to the status-code cases below.
        if (exception.Message.Contains("rejected as a duplicate", StringComparison.OrdinalIgnoreCase))
        {
            return new Diagnosis(
                "A bulk export is already running for this group on the source server — only one export per group " +
                "can run at a time. Wait for the in-flight export to finish, or cancel it, before starting another.",
                DiagnosisAction.SelfFix);
        }

        var statusMatch = StatusCodeRegex().Match(exception.Message);
        var statusCode = statusMatch.Success ? statusMatch.Groups[1].Value : null;

        return statusCode switch
        {
            "404" => new Diagnosis(
                "The configured export scope or resource type isn't supported at that endpoint — many FHIR servers " +
                "only support Group-level bulk export, not System-level. Check the Export Scope setting on this " +
                "source connection (try Group with a valid Group ID, or Patient), or confirm with the app " +
                "registration for this client which export scope is enabled.",
                DiagnosisAction.SelfFix),
            "401" or "403" => new Diagnosis(
                "One or more of the resource types configured for this export aren't authorized for this client's " +
                "registered scopes. Check the app registration for this client ID and confirm the resource type(s) " +
                "configured here are included in its granted scopes (Group export also needs system/Group.read).",
                DiagnosisAction.SelfFix),
            _ => new Diagnosis(
                "The FHIR server rejected the bulk export request. Check the Export Scope, resource types, and " +
                "Group ID (if applicable) configured on this source connection.",
                DiagnosisAction.SelfFix),
        };
    }

    [GeneratedRegex(@"returned (\d{3})")]
    private static partial Regex StatusCodeRegex();
}
