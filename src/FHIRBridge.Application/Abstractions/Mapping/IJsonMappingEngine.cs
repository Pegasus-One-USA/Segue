using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Mapping;

public interface IJsonMappingEngine
{
    /// <summary>
    /// Maps a source FHIR resource to destination columns. A field whose <c>JsonPath</c> is a reserved system token
    /// (starts with <c>@</c>, e.g. <c>@runId</c>, <c>@now</c>, <c>@resourceType</c>, <c>@sourceResourceId</c>) is
    /// resolved from <paramref name="systemValues"/> (pipeline/runtime context) instead of the source document — so
    /// audit/lineage columns are declared and filled like any other mapped field, with no special-casing in writers.
    /// </summary>
    MappingTestResultDto Map(
        string sourceJson,
        IReadOnlyCollection<MappingFieldDto> fields,
        IReadOnlyDictionary<string, object?>? systemValues = null);
}
