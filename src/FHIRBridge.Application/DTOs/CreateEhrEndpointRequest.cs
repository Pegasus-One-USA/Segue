using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

public sealed record CreateEhrEndpointRequest(
    SourceSystemType Vendor,
    string VendorEndpointId,
    string Name,
    string FhirBaseUrl,
    string FormatType,
    string Status,
    EhrEndpointType EndpointType = EhrEndpointType.MyChart,
    string? TokenEndpoint = null,
    string? ClientId = null,
    string? KeyId = null,
    string? JwksUrl = null,
    string? PracticeId = null);
