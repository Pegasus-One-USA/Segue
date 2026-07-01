using FHIRBridge.Runtime.Domain.Enums;

namespace FHIRBridge.Runtime.Application.DTOs;

public sealed record RuntimeDestinationConfiguration(
    RuntimeDestinationType DestinationType,
    string? ConnectionString,
    string? SchemaName);
