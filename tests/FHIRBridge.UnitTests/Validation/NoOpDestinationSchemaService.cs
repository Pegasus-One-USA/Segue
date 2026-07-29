using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.UnitTests.Validation;

/// <summary>
/// Test double for <see cref="IDestinationSchemaService"/> that always reports no introspected tables, so
/// <c>CreateMappingProfileRequestValidator</c>'s destination-schema cross-check no-ops. Used by tests that construct
/// <c>ConfigurationService</c> directly and only care about the other mapping-profile rules.
/// </summary>
public sealed class NoOpDestinationSchemaService : IDestinationSchemaService
{
    public Task<DestinationSchemaDto> GetSchemaAsync(Guid destinationId, CancellationToken cancellationToken) =>
        Task.FromResult(new DestinationSchemaDto(destinationId, []));

    public Task<DestinationSchemaProbeDto> ProbeSchemaAsync(
        DestinationConnectionProbeRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new DestinationSchemaProbeDto(true, null, []));
}
