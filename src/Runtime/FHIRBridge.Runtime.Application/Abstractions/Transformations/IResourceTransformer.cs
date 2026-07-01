using FHIRBridge.Runtime.Domain.ValueObjects;

namespace FHIRBridge.Runtime.Application.Abstractions.Transformations;

public interface IResourceTransformer
{
    Task<IReadOnlyList<ResourceEnvelope>> TransformAsync(
        string resourceType,
        IReadOnlyCollection<ResourceEnvelope> resources,
        CancellationToken cancellationToken);
}
