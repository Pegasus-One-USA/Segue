using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// Tests connectivity for an ad-hoc (not-yet-saved) <c>FhirRepository</c> destination — the FHIR analogue of
/// <see cref="ICsvDestinationConnectionTestService"/>'s SFTP test. Builds the auth header directly from the
/// request's raw credential fields rather than resolving a stored secret, since nothing has been saved yet.
/// </summary>
public interface IFhirDestinationConnectionTestService
{
    Task<ConnectionTestResultDto> TestConnectionAsync(
        FhirConnectionTestRequest request,
        CancellationToken cancellationToken);
}
