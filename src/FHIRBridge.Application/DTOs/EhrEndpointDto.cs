using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

public sealed record EhrEndpointDto(
    Guid Id,
    SourceSystemType Vendor,
    string VendorEndpointId,
    string Name,
    string FhirBaseUrl,
    string FormatType,
    string Status,
    DateTime CreatedOnUtc,
    string? CreatedBy,
    DateTime? ModifiedOnUtc,
    string? ModifiedBy,
    EhrEndpointType EndpointType = EhrEndpointType.MyChart);
