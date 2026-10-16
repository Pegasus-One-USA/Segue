using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Mappings;

public static class ApiClientMapper
{
    public static ApiClientDto ToDto(ApiClient client) =>
        new(
            client.Id,
            client.Name,
            client.ClientId,
            client.IsEnabled,
            client.LastUsedOnUtc,
            client.ReturnUrls.Select(x => new ApiClientReturnUrlDto(x.Id, x.Url, x.Label, x.MatchMode)).ToArray(),
            client.CreatedOnUtc,
            client.CreatedBy);
}
