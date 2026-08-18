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
}
