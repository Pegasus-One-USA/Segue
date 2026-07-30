namespace FHIRBridge.Application.DTOs;

public sealed record SourceConfigurationDto(
    Guid Id,
    Guid ConnectionId,
    string Name,
    string[] Scopes,
    SourceRetrievalConfigurationDto? Retrieval = null);
