using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.DTOs;

namespace FHIRBridge.Runtime.Infrastructure.Workflows.EhrWrite;

/// <summary>
/// Which <see cref="IEhrWriteChannel"/> a vendor's writes go through: a registry, never a switch. Every vendor starts
/// from the plain FHIR channel; a vendor whose write API is not plain FHIR creates wraps it (eClinicalWorks sends
/// transaction Bundles, athenahealth calls athenaOne). A vendor that is not listed writes plain FHIR (Epic, Generic
/// FHIR).
///
/// <para>A test run (a Generic FHIR server standing in for a vendor) builds the vendor's own channel over the test
/// server's connection. eClinicalWorks' Bundles and Epic's creates are FHIR, which the test server takes as they are;
/// athenaOne's REST calls are not, so they go through <see cref="AthenaOneTestServer"/>
/// (<see cref="TestClient"/>).</para>
/// </summary>
public static class EhrWriteChannels
{
    private static readonly IReadOnlyDictionary<SourceSystemType, Func<FhirClientEhrWriteChannel, IEhrWriteChannel>> ByVendor =
        new Dictionary<SourceSystemType, Func<FhirClientEhrWriteChannel, IEhrWriteChannel>>
        {
            [SourceSystemType.Healow] = fhir => new EcwEhrWriteChannel(fhir),
            [SourceSystemType.Athenahealth] = fhir => new AthenaOneEhrWriteChannel(fhir, AthenaPracticeId(fhir)),
        };

    /// <summary>The client a test run of the vendor sends through, over the test server's own client.</summary>
    private static readonly IReadOnlyDictionary<SourceSystemType, Func<IFhirWriteClient, FhirSourceConfiguration, IFhirWriteClient>> TestClients =
        new Dictionary<SourceSystemType, Func<IFhirWriteClient, FhirSourceConfiguration, IFhirWriteClient>>
        {
            [SourceSystemType.Athenahealth] = (server, source) =>
                new AthenaOneTestServer(server, source.BaseUrl ?? string.Empty, AthenaOneTestServer.TestPracticeId),
        };

    public static IEhrWriteChannel For(SourceSystemType vendor, FhirClientEhrWriteChannel fhir) =>
        ByVendor.TryGetValue(vendor, out var build) ? build(fhir) : fhir;

    /// <summary>The client a test run of <paramref name="testedVendor"/> sends through: the test server's own, unless
    /// the vendor's write API is not FHIR.</summary>
    public static IFhirWriteClient TestClient(SourceSystemType testedVendor, IFhirWriteClient server, FhirSourceConfiguration source) =>
        TestClients.TryGetValue(testedVendor, out var build) ? build(server, source) : server;

    private static string AthenaPracticeId(FhirClientEhrWriteChannel fhir)
    {
        if (fhir.Options.IsTestRun)
        {
            return AthenaOneTestServer.TestPracticeId;
        }

        return fhir.Source.PracticeId is { Length: > 0 } practice
            ? practice
            : throw new InvalidOperationException(
                "The athenahealth EHR connection has no Practice ID, which every athenaOne write needs.");
    }

    /// <summary>The scopes a write connection requests: its own, plus the vendor's proprietary write scope when it has
    /// one and the connection does not already ask for it.</summary>
    public static IReadOnlyCollection<string> WriteScopes(IReadOnlyCollection<string> scopes, string? proprietaryApiScope) =>
        proprietaryApiScope is null || scopes.Contains(proprietaryApiScope, StringComparer.Ordinal)
            ? scopes
            : [.. scopes, proprietaryApiScope];
}
