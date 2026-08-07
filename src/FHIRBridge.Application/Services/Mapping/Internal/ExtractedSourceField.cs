using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services.Mapping.Internal;

/// <summary>
/// One flattened leaf field pulled out of a source JSON document by <see cref="JsonSchemaExtractor"/>.
/// </summary>
/// <param name="Path">Concrete JsonPath to this exact value, indices included (e.g. <c>$.identifier[0].value</c>).</param>
/// <param name="StructuralPath">Index-stripped, dot-joined path (e.g. <c>identifier.value</c>) used for
/// structural-similarity scoring so every array item shares one structural identity.</param>
/// <param name="NormalizedName">Cached, human-readable normalized form of the leaf property name (see
/// <see cref="FieldNormalizer"/>), used for name/synonym scoring.</param>
/// <param name="DataType">Inferred from the JSON value kind.</param>
/// <param name="SampleValue">Raw string form of the value, used by <see cref="ValuePatternAnalyzer"/>.</param>
public sealed record ExtractedSourceField(
    string Path,
    string StructuralPath,
    string NormalizedName,
    MappingValueType DataType,
    string? SampleValue);
