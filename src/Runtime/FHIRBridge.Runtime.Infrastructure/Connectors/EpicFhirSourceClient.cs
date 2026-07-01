using FHIRBridge.Runtime.Application.Abstractions.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Runtime.Infrastructure.Connectors;

/// <summary>
/// Epic (and, until the other vendors are re-enabled, the generic FHIR R4) source connector. All transport
/// behavior — paginated search, retry, throttling — lives in <see cref="FhirSourceConnectorBase"/>; this type
/// only supplies the Epic-specific surface. Other EHR vendors will subclass the same base, differing only in
/// their display name and (if needed) request headers or URL shape.
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
