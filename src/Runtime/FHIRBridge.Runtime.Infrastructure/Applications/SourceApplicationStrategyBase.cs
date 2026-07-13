using FHIRBridge.Runtime.Application.Abstractions.Applications;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.SharedKernel.Enums;

namespace FHIRBridge.Runtime.Infrastructure.Applications;

/// <summary>
/// Shared configuration checks for the application-type strategies. Concrete strategies declare the type they handle,
/// their <see cref="SourceApplicationDescriptor"/>, and how they acquire a token (delegating to the appropriate
/// token provider), and layer type-specific validation on top of the common FHIR-base / identifier / endpoint checks
/// provided here.
/// </summary>
public abstract class SourceApplicationStrategyBase : ISourceApplicationStrategy
{
    public abstract ApplicationType Handles { get; }

    public abstract SourceApplicationDescriptor Describe();

    public abstract Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken);

    /// <summary>No patient context by default; interactive strategies override to expose the launched patient id.</summary>
    public virtual Task<string?> GetPatientContextAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    /// <summary>No resolved-base-url override by default; interactive strategies override to expose it.</summary>
    public virtual Task<string?> GetResolvedBaseUrlAsync(FhirSourceConfiguration source, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public SourceApplicationValidationResult Validate(FhirSourceConfiguration source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(source.BaseUrl))
        {
            errors.Add("A FHIR base URL is required.");
        }

        ValidateCore(source, errors);
        return SourceApplicationValidationResult.FromErrors(errors);
    }

    /// <summary>Adds the type-specific validation errors on top of the common checks.</summary>
    protected abstract void ValidateCore(FhirSourceConfiguration source, List<string> errors);

    protected static void RequireClientId(FhirSourceConfiguration source, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(source.ClientId))
        {
            errors.Add("A client ID is required.");
        }
    }

    protected static void RequireAuthorizationEndpoint(FhirSourceConfiguration source, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(source.AuthorizationEndpoint))
        {
            errors.Add("An authorization endpoint is required for interactive (authorization_code) flows.");
        }
    }

    protected static void RequireTokenEndpoint(FhirSourceConfiguration source, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(source.TokenEndpoint))
        {
            errors.Add("A token endpoint is required.");
        }
    }
}
