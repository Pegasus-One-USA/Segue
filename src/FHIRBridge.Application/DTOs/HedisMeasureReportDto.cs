namespace FHIRBridge.Application.DTOs;

public sealed record HedisMeasureReportDto(
    string ResourceType,
    string Id,
    string Status,
    string Type,
    string Measure,
    HedisMeasurePeriodDto Period,
    IReadOnlyList<HedisMeasureGroupDto> Group);

public sealed record HedisMeasurePeriodDto(
    DateTime Start,
    DateTime End);

public sealed record HedisMeasureGroupDto(
    string Code,
    string Display,
    int Denominator,
    int Numerator,
    decimal Score,
    IReadOnlyList<HedisMeasurePopulationDto> Population);

public sealed record HedisMeasurePopulationDto(
    string Code,
    int Count);
