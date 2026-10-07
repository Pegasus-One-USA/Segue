using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Runtime.Infrastructure.Workflows.EhrWrite;

/// <summary>
/// Which <see cref="IEhrWriteChannel"/> a vendor's writes go through: a registry, never a switch. Every vendor starts
/// from the plain FHIR channel; a vendor whose write API is not plain FHIR creates wraps it (eClinicalWorks sends
/// transaction Bundles, athenahealth calls athenaOne). A vendor that is not listed writes plain FHIR (Epic).
/// </summary>
public static class EhrWriteChannels
{
    private static readonly IReadOnlyDictionary<SourceSystemType, Func<FhirClientEhrWriteChannel, IEhrWriteChannel>> ByVendor =
        new Dictionary<SourceSystemType, Func<FhirClientEhrWriteChannel, IEhrWriteChannel>>
        {
            [SourceSystemType.Healow] = fhir => new EcwEhrWriteChannel(fhir),
            [SourceSystemType.Athenahealth] = fhir => new AthenaOneEhrWriteChannel(
                fhir,
                fhir.Source.PracticeId is { Length: > 0 } practice
                    ? practice
                    : throw new InvalidOperationException(
                        "The athenahealth EHR connection has no Practice ID, which every athenaOne write needs.")),
        };

    public static IEhrWriteChannel For(SourceSystemType vendor, FhirClientEhrWriteChannel fhir) =>
        ByVendor.TryGetValue(vendor, out var build) ? build(fhir) : fhir;

    /// <summary>The scopes a write connection requests: its own, plus the vendor's proprietary write scope when it has
    /// one and the connection does not already ask for it.</summary>
    public static IReadOnlyCollection<string> WriteScopes(IReadOnlyCollection<string> scopes, string? proprietaryApiScope) =>
        proprietaryApiScope is null || scopes.Contains(proprietaryApiScope, StringComparer.Ordinal)
            ? scopes
            : [.. scopes, proprietaryApiScope];
}
