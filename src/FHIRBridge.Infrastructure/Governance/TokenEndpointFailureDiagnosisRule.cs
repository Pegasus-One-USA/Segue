using FHIRBridge.Governance;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Diagnoses SMART/OAuth2 token-exchange failures from any source connector — Epic (see
/// <c>EpicAccessTokenProvider</c>), Healow/generic SMART (<c>SmartAuthorizationCodeTokenProvider</c>), MEDITECH
/// Greenfield (<c>MeditechGreenfieldTokenProvider</c>), and generic OAuth2 Backend Services
/// (<c>OAuth2ClientCredentialsTokenProvider</c>) all throw the same message shape: "{Provider} token endpoint
/// returned {status} ({reason}). {body}" or "... returned an empty response/token.". Matches by message shape
/// rather than a direct dependency on the Runtime.Infrastructure connectors that throw it, so this rule lives in
/// FHIRBridge.Infrastructure without needing a new project reference — and covers every vendor/provider at once
/// instead of one rule per vendor.
/// </summary>
public sealed class TokenEndpointFailureDiagnosisRule : IFailureDiagnosisRule
{
    public bool Matches(Exception exception) =>
        exception.Message.Contains("token endpoint returned", StringComparison.OrdinalIgnoreCase);

    public Diagnosis Diagnose(Exception exception)
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
