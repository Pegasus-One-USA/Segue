using FHIRBridge.Application.Abstractions.Governance;

namespace FHIRBridge.Application.Services;

public sealed class PassThroughDeIdentificationService : IDeIdentificationService
{
    public Task<string> DeIdentifyAsync(
        DeIdentificationRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(request.RawJson);
    }
}
