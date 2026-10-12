using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

/// <summary>CRUD for <see cref="Domain.Entities.DeIdentificationProfile"/> plus a preview that evaluates one
/// profile's rules against a hand-supplied sample — used by the Transformation Rules screen's profile
/// picker/creator and its Preview panel.</summary>
public interface IDeIdentificationProfileService
{
    Task<List<DeIdentificationProfileDto>> ListAsync(CancellationToken cancellationToken);

    Task<DeIdentificationProfileDto> CreateAsync(
        CreateDeIdentificationProfileRequest request, CancellationToken cancellationToken);

    Task<DeIdentificationPreviewResult> PreviewAsync(
        Guid profileId, DeIdentificationPreviewRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes every rule of the de-identification policy <paramref name="workflowId"/> owns — what the workflow
    /// builder calls when the workflow's last De-identification step is removed from the canvas.
    ///
    /// Scope: only the policy NAMED with this workflow's id (the builder creates exactly one per workflow, and the
    /// name is its only link to the workflow), never a policy the caller names by id. Refused — nothing deleted —
    /// when anything outside this workflow still uses that policy. The policy itself is kept, empty: adding a
    /// De-identification step again starts with no rules.
    /// </summary>
    Task<WorkflowDeIdentificationRuleDeletionResult> DeleteWorkflowPolicyRulesAsync(
        Guid workflowId, CancellationToken cancellationToken);
}
