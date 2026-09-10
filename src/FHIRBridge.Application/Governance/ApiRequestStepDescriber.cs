using System.Text.RegularExpressions;

namespace FHIRBridge.Application.Governance;

/// <summary>
/// Turns one logged HTTP call into a plain-language description of what the system was doing at that moment —
/// "Checking EHR sign-in status" rather than <c>GET /api/v1/workflows/{guid}/token-status</c>.
/// <para>Reading a correlation trace otherwise means knowing this codebase's route table by heart: the URLs are
/// near-identical (they differ only in a trailing segment behind the same workflow guid), so the one thing that
/// distinguishes the rows is the hardest part to scan. This is what makes an execution readable to someone who
/// is not the person who wrote the endpoints.</para>
/// <para>DERIVED at read time, deliberately not stored. It costs nothing to recompute, it applies retroactively to
/// every row already written, and the wording can be improved later without a migration or a backfill — none of
/// which would be true of a persisted column. The inputs it needs (direction, method, URL) are all already on the
/// row.</para>
/// </summary>
public static class ApiRequestStepDescriber
{
    /// <summary>Matches a FHIR resource type as the final path segment (e.g. <c>.../R4/Observation</c>) — FHIR
    /// resource types are PascalCase by specification, which is what distinguishes them from the lowercase
    /// route segments around them.</summary>
    private static readonly Regex FhirResourceSegment =
        new(@"/(?<resource>[A-Z][A-Za-z]+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Describe(string? method, string? url, string? direction)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "Unknown step";
        }

        // A CORS preflight is the browser's own, not the app's — labelling it as the call it precedes would
        // double-count every state-changing request in the trace.
        if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            return "Browser preflight check";
        }

        var isInbound = string.Equals(direction, "Inbound", StringComparison.OrdinalIgnoreCase);
        return isInbound ? DescribeInbound(url) : DescribeOutbound(url);
    }

    /// <summary>A call made TO this API — by a third-party app, or by the portal.</summary>
    private static string DescribeInbound(string url) => url switch
    {
        _ when Has(url, "/validate-run") => "Validating run parameters",
        _ when Has(url, "/token-status") => "Checking EHR sign-in status",
        _ when Has(url, "/discard-token") => "Discarding the cached EHR token",
        _ when Has(url, "/public-standalone-url") || Has(url, "/public-patient-standalone-url")
            => "Starting EHR sign-in",
        _ when Has(url, "/public-launch-context") => "Preparing the EHR launch",
        _ when Has(url, "/oauth/launch") => "Handing the browser to the EHR",
        _ when Has(url, "/oauth/callback") => "Completing EHR sign-in",
        _ when Has(url, "/latest-launch-result") || Has(url, "/launch-result") => "Reading the launch result",
        _ when Has(url, "/ehr-public-endpoints") => "Listing available hospitals",
        _ when Has(url, "/checkpoint-result") || Has(url, "/checkpoint-url") => "Resolving a checkpoint",
        _ when Has(url, "/destination-data") => "Reading destination data",
        // Checked after the more specific workflow routes above, all of which also contain "/workflows/".
        _ when Has(url, "/workflow-runs") => "Reading execution history",
        _ when Has(url, "/runs") => "Reading previous runs",
        _ when Has(url, "/run") => "Triggering the workflow run",
        _ when Has(url, "/workflows") => "Reading workflow configuration",
        _ => "API call"
    };

    /// <summary>A call this system made OUT to an EHR, destination or terminology server.</summary>
    private static string DescribeOutbound(string url) => url switch
    {
        _ when Has(url, "/.well-known/smart-configuration") => "Discovering the EHR's SMART configuration",
        _ when Has(url, "/.well-known/openid-configuration") => "Discovering the EHR's OpenID configuration",
        _ when Has(url, "/PublicKeys") || Has(url, "/OIDC") => "Fetching the EHR's signing keys",
        _ when Has(url, "/metadata") => "Discovering the EHR's capabilities",
        _ when Has(url, "$export") => "Starting a bulk export",
        _ when Has(url, "/token") => "Acquiring an EHR access token",
        _ when Has(url, "/authorize") => "Requesting EHR authorization",
        _ when FhirResourceSegment.Match(url) is { Success: true } match
            => $"Fetching {match.Groups["resource"].Value} records",
        _ => "Calling the EHR"
    };

    private static bool Has(string url, string fragment) =>
        url.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
