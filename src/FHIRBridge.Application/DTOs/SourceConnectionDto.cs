using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Enums;

namespace FHIRBridge.Application.DTOs;

public sealed record SourceConnectionDto(
    Guid Id,
    string Name,
    SourceSystemType SourceSystemType,
    string BaseUrl,
    SourceAuthenticationDto Authentication,
    bool IsEnabled,
    ApplicationType? ApplicationType = null,
    SourceInteractiveConfigurationDto? Interactive = null,
    SourceRetrievalConfigurationDto? Retrieval = null);
