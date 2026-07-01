using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Mapping;

public interface IJsonMappingEngine
{
    MappingTestResultDto Map(string sourceJson, IReadOnlyCollection<MappingFieldDto> fields);
}
