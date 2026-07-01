using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Abstractions.Destinations;

public interface IConfiguredDestinationWriterFactory
{
    IConfiguredDestinationWriter Create(DestinationType destinationType);
}
