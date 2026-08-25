using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// Tests an ad-hoc Azure Blob Storage connection (the destination form's Test Connection button, before anything
/// is saved) by building the container client for the supplied auth mode + credentials and doing a real
/// reachability round-trip (container <c>ExistsAsync</c>). Never throws for connection failures; returns
/// <c>Connected=false</c> + <c>Error</c> instead, matching the FHIR / Medplum / Mongo / SFTP test contracts.
/// </summary>
public interface IBlobDestinationConnectionTestService
{
    Task<ConnectionTestResultDto> TestConnectionAsync(
        BlobConnectionTestRequest request,
        CancellationToken cancellationToken);
}
