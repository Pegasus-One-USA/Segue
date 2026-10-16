namespace FHIRBridge.Application.DTOs;

public sealed record DeIdentificationProfileDto(Guid Id, string Name, string? Description);

public sealed record CreateDeIdentificationProfileRequest(string Name, string? Description = null);

/// <summary>Evaluates one profile's pre-mapping rules against a hand-supplied sample resource — no destination,
/// no pipeline run, no persistence. Lets an admin see what a profile actually does before assigning it to a
/// real destination.</summary>
public sealed record DeIdentificationPreviewRequest(string ResourceType, string SampleJson);

public sealed record DeIdentificationPreviewResult(string RedactedJson);

/// <summary>Outcome of deleting the rules of a workflow's own de-identification policy: how many were deleted, or —
/// when <see cref="UsedElsewhere"/> is not empty — nothing, because the policy is still used outside the workflow.</summary>
public sealed record WorkflowDeIdentificationRuleDeletionResult(int Deleted, IReadOnlyList<string> UsedElsewhere);
