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

    // Produces no hops, so nothing ever asks it to redact a mapped value; returned unchanged for completeness.
    public object? DeIdentifyValue(object? value, DeIdentificationFieldHop hop) => value;
}
