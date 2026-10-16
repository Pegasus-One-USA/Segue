namespace FHIRBridge.Application.DTOs;

/// <summary>Result of validating an external-trigger request's credentials, Return URL and Referer — the
/// "Phase A" check described in ExternalWorkflowTriggerService's remarks. Non-null only when every check
/// passed, at which point <see cref="ValidatedReturnUrl"/> is safe to redirect to even if a later step
/// (unknown workflow, execution failure) fails.</summary>
public sealed record ExternalTriggerValidationResult(Guid ApiClientId, string ValidatedReturnUrl);
