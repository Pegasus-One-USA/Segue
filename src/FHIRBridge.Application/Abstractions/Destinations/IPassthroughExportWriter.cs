using FHIRBridge.Application.DTOs;
using FHIRBridge.Runtime.Domain.ValueObjects;

namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// Formats a stateless pass-through read into the tenant's admin-configured destination format, reusing the same
/// mapping engine, normalization, and destination writers as the configured pipeline. Returns <c>null</c> when the
/// tenant has no usable destination/mapping configured (or the destination is ambiguous) so the caller can fall back
/// to returning the raw FHIR Bundle. Kept separate from the configured pipeline so that flow is untouched.
/// </summary>
public interface IPassthroughExportWriter
{
    /// <summary>
    /// Maps <paramref name="resources"/> using the tenant's mapping profiles bound to the resolved destination and
    /// emits them in that destination's format. <paramref name="destinationId"/> selects the destination explicitly;
    /// when null, the tenant's single enabled destination is used (ambiguous/none → returns null).
    /// </summary>
    Task<PassthroughExportResult?> WriteAsync(
        Guid tenantId,
        Guid? destinationId,
        IReadOnlyCollection<ResourceEnvelope> resources,
        CancellationToken cancellationToken);
}
