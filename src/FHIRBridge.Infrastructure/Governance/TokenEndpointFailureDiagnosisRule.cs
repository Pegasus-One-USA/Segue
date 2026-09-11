using FHIRBridge.Governance;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Diagnoses SMART/OAuth2 token-exchange failures from any source connector, for every vendor — SMART Backend
/// Services (<c>SmartBackendServicesTokenProvider</c>), interactive SMART
/// (<c>SmartAuthorizationCodeTokenProvider</c>), MEDITECH Greenfield (<c>MeditechGreenfieldTokenProvider</c>) and
/// generic OAuth2 Backend Services (<c>OAuth2ClientCredentialsTokenProvider</c>). Each of those names the failing
/// vendor from the source's own type, so the diagnosis it produces here is vendor-accurate.
/// <para>
/// Preferred path: the connectors throw <see cref="TokenEndpointException"/>, which carries the HTTP status as a
/// NUMBER plus a categorized <see cref="TokenEndpointFailureKind"/>. That is what lets an upstream outage (5xx) be
/// described as an outage instead of being handed the credentials advice — the bug this rule used to have, where
/// every status that wasn't literally accompanied by the text "invalid_client" fell through to
/// "check the token endpoint URL, client credentials, and scopes".
/// </para>
/// <para>
/// Fallback path: the original message-shape matching is kept for any exception NOT yet carrying the typed status
/// (a third-party/legacy throw site, or a connector added later that forgets). It can only ever be as good as it
/// was before, never worse, and it means adopting the typed exception is not all-or-nothing.
/// </para>
/// Matching by exception type also fixes a gap the message matching had: the generic client-credentials provider
/// worded its failure "OAuth2 token request returned …", which never matched "token endpoint returned" at all.
/// </summary>
public sealed class TokenEndpointFailureDiagnosisRule : IFailureDiagnosisRule
{
    public bool Matches(Exception exception) =>
        exception is TokenEndpointException ||
        exception.Message.Contains("token endpoint returned", StringComparison.OrdinalIgnoreCase);

    public Diagnosis Diagnose(Exception exception)
    {
        return exception is TokenEndpointException typed
            ? DiagnoseTyped(typed)
            : DiagnoseFromMessage(exception);
    }

    /// <summary>
    /// Status-driven diagnosis. <see cref="TokenEndpointException.UserMessage"/> is already the author-written,
    /// client-safe sentence for exactly this failure, so it is reused verbatim as the cause rather than restated
    /// here — one wording, so the Errors screen, the API response and a pipeline run's error list can never
    /// disagree about the same failure.
    /// </summary>
    private static Diagnosis DiagnoseTyped(TokenEndpointException exception)
    {
        // Every kind stays SelfFix, including an outage. DiagnosisAction.SelfFix explicitly covers "unreachable
        // endpoint" (see its own remarks) and means "the customer is the one who acts" — retry, or chase the
        // vendor. ContactSupport means "needs the FHIRBridge team", so routing a vendor outage there would raise
        // a support ticket against us every time a vendor has a maintenance window. The distinction the operator
        // needs is in the wording, not the badge.
        return new Diagnosis(exception.UserMessage, DiagnosisAction.SelfFix);
    }

    /// <summary>
    /// The pre-existing message-shape heuristic, unchanged in behaviour, for throw sites that don't carry the typed
    /// status yet.
    /// </summary>
    private static Diagnosis DiagnoseFromMessage(Exception exception)
    {
        if (exception.Message.Contains("invalid_client", StringComparison.OrdinalIgnoreCase))
        {
            return new Diagnosis(
                "Check the client ID and private key/secret configured on this source connection.",
                DiagnosisAction.SelfFix);
        }

        if (exception.Message.Contains("empty", StringComparison.OrdinalIgnoreCase))
        {
            return new Diagnosis(
                "The identity provider did not return a usable token — check the token endpoint URL and scopes " +
                "configured on this source connection.",
                DiagnosisAction.SelfFix);
        }

        return new Diagnosis(
            "The identity provider rejected this request — check the token endpoint URL, client credentials, " +
            "and scopes configured on this source connection.",
            DiagnosisAction.SelfFix);
    }
}
