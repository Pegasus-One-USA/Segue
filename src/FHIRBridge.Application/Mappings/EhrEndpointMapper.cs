using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Mappings;

public static class EhrEndpointMapper
{
    public static EhrEndpointDto ToDto(EhrEndpoint endpoint) =>
        new(
            endpoint.Id,
            endpoint.Vendor,
            endpoint.VendorEndpointId,
            endpoint.Name,
            endpoint.FhirBaseUrl,
            endpoint.FormatType,
            endpoint.Status,
            endpoint.CreatedOnUtc,
            endpoint.CreatedBy,
            endpoint.ModifiedOnUtc,
            endpoint.ModifiedBy);
}
