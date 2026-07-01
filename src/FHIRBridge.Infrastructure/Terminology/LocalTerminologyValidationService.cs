using FHIRBridge.Application.Abstractions.Terminology;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>
/// Validates codes against the in-process <see cref="UsCoreValueSetCatalog"/> (closed FHIR/US Core required-binding
/// ValueSets). Returns null for any ValueSet it doesn't know, so the composite can defer to the FHIR server.
/// </summary>
public sealed class LocalTerminologyValidationService : ITerminologyValidationService
{
    public Task<TerminologyValidationResult?> ValidateCodeAsync(
        string valueSetUrl,
        string? system,
        string code,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(valueSetUrl) || string.IsNullOrWhiteSpace(code) ||
            !UsCoreValueSetCatalog.TryGet(valueSetUrl, out var definition))
        {
            return Task.FromResult<TerminologyValidationResult?>(null);
        }

        var isValid = definition.Codes.Contains(code.Trim());
        var message = isValid
            ? null
            : $"Code '{code}' is not a member of {valueSetUrl}.";

        return Task.FromResult<TerminologyValidationResult?>(new TerminologyValidationResult(isValid, message, "Local"));
    }
}
