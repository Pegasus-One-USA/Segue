namespace FHIRBridge.Governance;

/// <summary>
/// Default <see cref="IFailureDiagnosisClassifier"/>. Evaluates registered <see cref="IFailureDiagnosisRule"/>
/// instances in order (first match wins) before falling back to a category-based default — the same "unknown
/// stays unknown, known signatures win" shape as <see cref="DefaultExceptionClassifier"/>.
/// </summary>
public sealed class DefaultFailureDiagnosisClassifier : IFailureDiagnosisClassifier
{
    private readonly IReadOnlyList<IFailureDiagnosisRule> _rules;

    public DefaultFailureDiagnosisClassifier(IEnumerable<IFailureDiagnosisRule>? rules = null)
    {
        _rules = rules?.ToList() ?? [];
    }

    public Diagnosis Diagnose(Exception exception, ErrorCategory fallbackCategory)
    {
        foreach (var rule in _rules)
        {
            if (rule.Matches(exception))
            {
                return rule.Diagnose(exception);
            }
        }

        return DefaultFor(fallbackCategory);
    }

    /// <summary>
    /// Category-based fallback for when no specific signature rule matched. Deliberately conservative: only
    /// categories where "the customer's own input/config" is a plausible root cause are marked SelfFix; everything
    /// else (including Database and Business, which can just as easily be FHIRBridge's own fault) defaults to
    /// Unknown rather than guessing.
    /// </summary>
    private static Diagnosis DefaultFor(ErrorCategory category) => category switch
    {
        ErrorCategory.Validation =>
            new Diagnosis("Some information in the request was invalid.", DiagnosisAction.SelfFix),
        ErrorCategory.Authentication =>
            new Diagnosis("We could not verify your identity for this request.", DiagnosisAction.SelfFix),
        ErrorCategory.Authorization =>
            new Diagnosis("This account does not have permission to perform this action.", DiagnosisAction.SelfFix),
        ErrorCategory.Network or ErrorCategory.ExternalSystem =>
            new Diagnosis("A connected system did not respond as expected.", DiagnosisAction.SelfFix),
        _ =>
            new Diagnosis("An unexpected error occurred while processing this request.", DiagnosisAction.Unknown),
    };
}
