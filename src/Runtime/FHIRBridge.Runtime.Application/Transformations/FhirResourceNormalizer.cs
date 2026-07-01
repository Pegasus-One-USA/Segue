using System.Text.Json;
using FHIRBridge.Runtime.Application.Abstractions.Transformations;
using FHIRBridge.Runtime.Domain.ValueObjects;

namespace FHIRBridge.Runtime.Application.Transformations;

public sealed class FhirResourceNormalizer : IResourceTransformer
{
    public Task<IReadOnlyList<ResourceEnvelope>> TransformAsync(
        string resourceType,
        IReadOnlyCollection<ResourceEnvelope> resources,
        CancellationToken cancellationToken)
    {
        var transformed = resources
            .Select(resource =>
            {
                using var document = JsonDocument.Parse(resource.RawJson);
                var compactJson = JsonSerializer.Serialize(document.RootElement);

                return resource with
                {
                    RawJson = compactJson
                };
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<ResourceEnvelope>>(transformed);
    }
}
