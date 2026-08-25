using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// Tests an ad-hoc Medplum connection (the destination form's Test Connection button, before anything is saved)
/// by minting an OAuth2 token from the supplied credentials and doing a real <c>GET {baseUrl}/metadata</c>.
/// Never throws for connection failures; returns <c>Connected=false</c> + <c>Error</c> instead, matching
/// <see cref="IFhirDestinationConnectionTestService"/>'s and <see cref="ICsvDestinationConnectionTestService"/>'s
/// contracts.
/// </summary>
public interface IMedplumDestinationConnectionTestService
{
    Task<ConnectionTestResultDto> TestConnectionAsync(
        MedplumConnectionTestRequest request,
        CancellationToken cancellationToken);
}
