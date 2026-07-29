namespace FHIRBridge.Application.DTOs;

public sealed record CreateSourceConfigurationRequest(
    Guid ConnectionId,
    string Name,
    string[] Scopes,
    SourceRetrievalConfigurationDto? Retrieval = null);
