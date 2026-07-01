using FHIRBridge.Runtime.Application.DTOs;

namespace FHIRBridge.Runtime.Application.Abstractions.Auth;

public interface IFhirAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(
        FhirSourceConfiguration source,
        CancellationToken cancellationToken);
}
