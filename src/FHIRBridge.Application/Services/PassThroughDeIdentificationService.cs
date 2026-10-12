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

    // This service redacts nothing and produces no hops, so the Mapping node never defers a column through it. If it
    // is ever asked anyway (e.g. a host that registers it for Mapping while another service ran De-identification),
    // it cannot honour the hop's rule — so it fails closed: null (the value is dropped, and the Mapping node records
    // that as a failed de-identification), never the unredacted value it was handed.
    public object? DeIdentifyValue(object? value, DeIdentificationFieldHop hop) => null;

    // It applies no rules, so it has none to offer: a De-identification node relying on it fails closed.
    public Task<bool> HasRulesAsync(Guid profileId, CancellationToken cancellationToken) => Task.FromResult(false);
}
