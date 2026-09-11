using FHIRBridge.Runtime.Application.Abstractions.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Runtime.Infrastructure.Connectors;

/// <summary>
/// Epic source connector. All transport behavior — paginated search, retry, throttling — lives in
/// <see cref="FhirSourceConnectorBase"/>; this type only supplies the Epic-specific surface. Other EHR vendors
/// subclass the same base, differing only in their display name and (if needed) request headers or URL shape —
/// see <see cref="GenericFhirSourceClient"/>, which no longer routes generic FHIR sources through this class and
/// so no longer labels their logs as Epic's.
/// </summary>
public sealed class EpicFhirSourceClient : FhirSourceConnectorBase
{
    public EpicFhirSourceClient(
        HttpClient httpClient,
        IFhirAccessTokenProvider accessTokenProvider,
        IOptions<EpicFhirClientOptions>? options = null,
        ILogger<EpicFhirSourceClient>? logger = null)
        : base(httpClient, accessTokenProvider, options?.Value, logger)
    {
    }

    protected override string SourceDisplayName => "Epic FHIR";
}
