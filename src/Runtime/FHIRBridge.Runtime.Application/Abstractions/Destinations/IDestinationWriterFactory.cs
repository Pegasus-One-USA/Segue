using FHIRBridge.Runtime.Domain.Enums;

namespace FHIRBridge.Runtime.Application.Abstractions.Destinations;

public interface IDestinationWriterFactory
{
    IDestinationWriter Create(RuntimeDestinationType destinationType);
}
