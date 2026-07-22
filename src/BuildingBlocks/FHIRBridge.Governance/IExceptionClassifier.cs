namespace FHIRBridge.Governance;

/// <summary>
/// Maps an arbitrary <see cref="Exception"/> to an <see cref="ErrorCategory"/>. Kept as an injectable
/// strategy (not a hard-coded switch) so new categorization rules are added by registering a rule, never by
/// editing a central switch — consistent with the codebase's registry-over-switch convention.
/// </summary>
public interface IExceptionClassifier
{
    ErrorCategory Classify(Exception exception);
}

/// <summary>One ordered categorization rule: the first whose <see cref="Matches"/> returns true wins.</summary>
public sealed record ExceptionCategoryRule(ErrorCategory Category, Func<Exception, bool> Matches);
