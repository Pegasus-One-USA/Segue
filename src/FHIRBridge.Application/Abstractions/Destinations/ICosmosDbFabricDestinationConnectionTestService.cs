using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// Tests an ad-hoc Cosmos DB in Fabric connection (the destination form's Test Connection button, before
/// anything is saved) by reading the database and listing its containers. Never throws for connection failures;
/// returns <c>Connected=false</c> + <c>Error</c> instead, matching the Mongo / FHIR / Fabric test contracts.
/// </summary>
public interface ICosmosDbFabricDestinationConnectionTestService
{
    Task<CosmosDbFabricConnectionTestResultDto> TestConnectionAsync(
        CosmosDbFabricConnectionTestRequest request,
        CancellationToken cancellationToken);
}
