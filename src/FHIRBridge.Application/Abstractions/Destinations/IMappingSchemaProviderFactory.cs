using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Abstractions.Destinations;

public interface IMappingSchemaProviderFactory
{
    /// <summary>Throws <see cref="NotSupportedException"/> if no provider is registered for <paramref name="destinationType"/>.</summary>
    IMappingSchemaProvider Create(DestinationType destinationType);
}
