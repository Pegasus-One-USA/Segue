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

                // Make the resource FHIR-conformant (invariant fixes + malformed-string cleanup) before it reaches a
                // destination writer, so strict FHIR servers don't 400 individual records. No-op for clean data.
                var conformantJson = FhirConformanceSanitizer.Sanitize(resource.ResourceType, compactJson) ?? compactJson;

                return resource with
                {
                    RawJson = conformantJson
                };
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<ResourceEnvelope>>(transformed);
    }
}
