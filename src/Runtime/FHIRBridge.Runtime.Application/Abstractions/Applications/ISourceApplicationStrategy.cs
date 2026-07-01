using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.SharedKernel.Enums;

namespace FHIRBridge.Runtime.Application.Abstractions.Applications;

/// <summary>
/// Encapsulates everything that varies by <see cref="ApplicationType"/> for a source connection: the OAuth flow it
/// describes, its configuration validation, and how it acquires an access token. There is one strategy per type,
/// resolved from <see cref="ISourceApplicationStrategyRegistry"/>. Adding a new application type is a new
/// implementation plus a single registration — the composite token provider, pipeline, and callback handling stay
/// closed for modification (enforced by the no-switch-on-ApplicationType architecture test).
/// </summary>
/// <remarks>
/// The interactive-flow members — authorize-request construction, callback completion and context resolution — are
/// added onto this same interface as those flows are built out, so dispatch stays registry-based.
/// </remarks>
public interface ISourceApplicationStrategy
{
    /// <summary>The single application type this strategy is responsible for.</summary>
    ApplicationType Handles { get; }

    /// <summary>The invariant description of this application type's OAuth flow and configuration surfaces.</summary>
    SourceApplicationDescriptor Describe();

    /// <summary>Validates a source configuration against this application type's rules (the wizard "Validate" step).</summary>
    SourceApplicationValidationResult Validate(FhirSourceConfiguration source);

    /// <summary>Acquires an access token for a source configured as this application type.</summary>
    Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken);
}
