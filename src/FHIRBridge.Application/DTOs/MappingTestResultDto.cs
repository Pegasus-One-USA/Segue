namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Result of mapping a single source resource. <see cref="Values"/> is the first parent row
/// (backward-compatible scalar view). <see cref="Rows"/> holds all parent rows produced by
/// RepeatParent fan-out, and <see cref="ChildTables"/> holds SeparateDestination output.
/// </summary>
public sealed record MappingTestResultDto(
    IReadOnlyDictionary<string, object?> Values,
    IReadOnlyList<string> Errors,
    IReadOnlyList<IReadOnlyDictionary<string, object?>>? Rows = null,
    IReadOnlyList<MappingChildTableDto>? ChildTables = null);

public sealed record MappingChildTableDto(
    string Name,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);
