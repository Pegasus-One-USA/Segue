using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Enums;

namespace FHIRBridge.Application.DTOs;

public sealed record CreateSourceConnectionRequest(
    string Name,
    SourceSystemType SourceSystemType,
    string BaseUrl,
    SourceAuthenticationDto Authentication,
    ApplicationType? ApplicationType = null,
    SourceInteractiveConfigurationDto? Interactive = null,
    SourceRetrievalConfigurationDto? Retrieval = null);
