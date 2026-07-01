namespace FHIRBridge.Runtime.Application.Abstractions.Applications;

/// <summary>
/// Outcome of validating a source configuration against the rules of its application type (the wizard "Validate"
/// step). <see cref="Errors"/> is empty when the configuration is complete enough to attempt a test launch.
/// </summary>
public sealed record SourceApplicationValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static SourceApplicationValidationResult Success { get; } = new(true, []);

    /// <summary>Builds a result from collected errors — valid when the list is empty.</summary>
    public static SourceApplicationValidationResult FromErrors(IReadOnlyList<string> errors) =>
        new(errors.Count == 0, errors);
}
