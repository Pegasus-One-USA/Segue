using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Enums;

namespace FHIRBridge.Application.DTOs;

/// <param name="Access">What the connection may be used for. Null means Read on create and "keep what is saved" on
/// update: the workflow canvas rebuilds this whole request on every save, and older portal bundles and the workflow
/// copy path do not send it, so a missing value must never downgrade a write connection.</param>
public sealed record CreateSourceConnectionRequest(
    string Name,
    SourceSystemType SourceSystemType,
    string BaseUrl,
    SourceAuthenticationDto Authentication,
    ApplicationType? ApplicationType = null,
    SourceInteractiveConfigurationDto? Interactive = null,
    SourceRetrievalConfigurationDto? Retrieval = null,
    SourceConnectionAccess? Access = null);
