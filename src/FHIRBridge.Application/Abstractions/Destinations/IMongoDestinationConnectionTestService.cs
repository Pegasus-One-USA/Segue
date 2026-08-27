using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// Tests an ad-hoc MongoDB connection (the destination form's Test Connection button, before anything is saved)
/// by opening a client on the supplied connection string and running a <c>ping</c> against its database. Never
/// throws for connection failures; returns <c>Connected=false</c> + <c>Error</c> instead, matching the FHIR /
/// Medplum / SFTP test contracts.
/// </summary>
public interface IMongoDestinationConnectionTestService
{
    Task<MongoConnectionTestResultDto> TestConnectionAsync(
        MongoConnectionTestRequest request,
        CancellationToken cancellationToken);
}
