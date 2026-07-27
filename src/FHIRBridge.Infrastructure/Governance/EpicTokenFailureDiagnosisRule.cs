using FHIRBridge.Governance;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Diagnoses Epic SMART Backend Services token-exchange failures (see
/// <c>EpicAccessTokenProvider.BuildFailureMessageAsync</c>, which embeds the token endpoint's raw response body
/// in the thrown <see cref="InvalidOperationException"/>'s message). Matches by message shape rather than a
/// direct dependency on the Runtime.Infrastructure connector that throws it, so this rule lives in
/// FHIRBridge.Infrastructure without needing a new project reference.
/// </summary>
public sealed class EpicTokenFailureDiagnosisRule : IFailureDiagnosisRule
{
    public bool Matches(Exception exception) =>
        exception.Message.Contains("Epic token endpoint", StringComparison.OrdinalIgnoreCase);

    public Diagnosis Diagnose(Exception exception) =>
        exception.Message.Contains("invalid_client", StringComparison.OrdinalIgnoreCase)
            ? new Diagnosis(
                "Check the client ID and private key on this source connection.",
                DiagnosisAction.SelfFix)
            : new Diagnosis(
                "The Epic endpoint rejected this request — check the token endpoint URL and scopes on this source connection.",
                DiagnosisAction.SelfFix);
}
