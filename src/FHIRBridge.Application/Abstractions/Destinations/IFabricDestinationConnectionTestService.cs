using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// Tests an ad-hoc Microsoft Fabric connection (the destination form's Test Connection button, before anything is
/// saved) by resolving the Entra identity and doing a real reachability round-trip against each endpoint the
/// chosen landing mode uses. Never throws for connection failures; returns <c>Connected=false</c> + <c>Error</c>
/// instead, matching the Blob / FHIR / Medplum / Mongo / SFTP test contracts.
/// </summary>
public interface IFabricDestinationConnectionTestService
{
    Task<FabricConnectionTestResultDto> TestConnectionAsync(
        FabricConnectionTestRequest request,
        CancellationToken cancellationToken);
}
