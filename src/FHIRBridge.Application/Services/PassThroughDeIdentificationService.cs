using FHIRBridge.Application.Abstractions.Governance;

namespace FHIRBridge.Application.Services;

public sealed class PassThroughDeIdentificationService : IDeIdentificationService
{
    public Task<DeIdentificationResult> DeIdentifyAsync(
        DeIdentificationRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new DeIdentificationResult(request.RawJson, []));
    }
}
