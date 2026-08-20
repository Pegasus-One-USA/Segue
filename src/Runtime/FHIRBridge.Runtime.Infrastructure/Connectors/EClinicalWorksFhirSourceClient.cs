using FHIRBridge.Runtime.Application.Abstractions.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Runtime.Infrastructure.Connectors;

/// <summary>
/// eClinicalWorks (Healow) FHIR R4 source connector. Reuses <see cref="FhirSourceConnectorBase"/>'s paginated
/// search/retry/throttle machinery, minus the base's default <c>_count</c> injection (see
/// <see cref="BuildSearchUrl"/>) — every other vendor-specific request-level header/query parameter or
/// per-resource-type search requirement remains unconfirmed against a live sandbox (see the ECW_Net
/// proof-of-concept's own "unverified" notes on scope/host shape). Add further overrides here (mirroring
/// AthenahealthFhirSourceClient) if a live run surfaces another real eCW-specific requirement.
/// </summary>
public sealed class EClinicalWorksFhirSourceClient : FhirSourceConnectorBase
{
    public EClinicalWorksFhirSourceClient(
        HttpClient httpClient,
        IFhirAccessTokenProvider accessTokenProvider,
        IOptions<EpicFhirClientOptions>? options = null,
        ILogger<EClinicalWorksFhirSourceClient>? logger = null)
        : base(httpClient, accessTokenProvider, options?.Value, logger)
    {
    }

    protected override string SourceDisplayName => "eClinicalWorks FHIR";

    /// <summary>
    /// eCW's FHIR R4 API rejects the standard SMART/FHIR <c>_count</c> pagination parameter outright — confirmed
    /// against a live search: "Unsupported query parameter(s): _count. These parameters are not allowed." The base
    /// class always injects one when the caller's own search parameters don't already carry it; this override
    /// drops that injection entirely for eCW instead. Pagination still works via whatever <c>next</c> Bundle link
    /// eCW returns — the base class's paging loop follows that unconditionally, independent of this URL's shape.
    /// </summary>
    protected override string BuildSearchUrl(
        string baseUrl,
        string resourceType,
        int searchCount,
        string? searchParameters)
    {
        var query = string.IsNullOrWhiteSpace(searchParameters)
            ? string.Empty
            : searchParameters.Trim().TrimStart('?');

        return $"{baseUrl.TrimEnd('/')}/{resourceType}?{query}";
    }
}
