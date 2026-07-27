namespace FHIRBridge.Governance;

/// <summary>
/// Computes a <see cref="Diagnosis"/> for a captured exception. Tries registered <see cref="IFailureDiagnosisRule"/>
/// instances first (specific, known failure signatures); falls back to a category-based default when nothing
/// matches, so callers always get a usable result.
/// </summary>
public interface IFailureDiagnosisClassifier
{
    Diagnosis Diagnose(Exception exception, ErrorCategory fallbackCategory);
}
