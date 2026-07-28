using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Application.Exceptions;

/// <summary>
/// Thrown when a request DTO fails a <c>FluentValidation</c> <see cref="FluentValidation.IValidator{T}"/> check.
/// Carries field-keyed messages so the API can return a structured response the UI can map back onto individual
/// form controls, instead of the single flat message every other <see cref="FHIRBridgeException"/> carries.
/// </summary>
public sealed class RequestValidationException : FHIRBridgeException
{
    public RequestValidationException(IReadOnlyDictionary<string, string[]> fieldErrors)
        : base(BuildDiagnosticMessage(fieldErrors), "Validation failed.")
    {
        FieldErrors = fieldErrors;
    }

    public IReadOnlyDictionary<string, string[]> FieldErrors { get; }

    private static string BuildDiagnosticMessage(IReadOnlyDictionary<string, string[]> fieldErrors) =>
        "Request validation failed: " +
        string.Join("; ", fieldErrors.Select(kv => $"{kv.Key}: {string.Join(", ", kv.Value)}"));
}
