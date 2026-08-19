namespace FHIRBridge.Application.DTOs;

public sealed record DeIdentificationProfileDto(Guid Id, string Name, string? Description);

public sealed record CreateDeIdentificationProfileRequest(string Name, string? Description = null);

/// <summary>Evaluates one profile's pre-mapping rules against a hand-supplied sample resource — no destination,
/// no pipeline run, no persistence. Lets an admin see what a profile actually does before assigning it to a
/// real destination.</summary>
public sealed record DeIdentificationPreviewRequest(string ResourceType, string SampleJson);

public sealed record DeIdentificationPreviewResult(string RedactedJson);
