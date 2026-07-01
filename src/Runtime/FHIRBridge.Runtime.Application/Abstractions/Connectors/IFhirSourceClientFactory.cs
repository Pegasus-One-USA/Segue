using FHIRBridge.Runtime.Domain.Enums;

namespace FHIRBridge.Runtime.Application.Abstractions.Connectors;

public interface IFhirSourceClientFactory
{
    IFhirSourceClient Create(RuntimeSourceType sourceType);
}
