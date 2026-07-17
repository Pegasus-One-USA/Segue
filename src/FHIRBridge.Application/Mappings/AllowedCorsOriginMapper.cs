using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Mappings;

public static class AllowedCorsOriginMapper
{
    public static AllowedCorsOriginDto ToDto(AllowedCorsOrigin origin) =>
        new(
            origin.Id,
            origin.OriginUrl,
            origin.Label,
            origin.CreatedOnUtc,
            origin.CreatedBy);
}
