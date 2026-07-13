using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

public sealed record CreateEhrEndpointRequest(
    SourceSystemType Vendor,
    string VendorEndpointId,
    string Name,
    string FhirBaseUrl,
    string FormatType,
    string Status);
