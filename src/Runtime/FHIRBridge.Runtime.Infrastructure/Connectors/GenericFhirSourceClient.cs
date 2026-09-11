using FHIRBridge.Runtime.Application.Abstractions.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Runtime.Infrastructure.Connectors;

/// <summary>
/// Generic FHIR R4 source connector: a plain, vendor-neutral FHIR server (HAPI, Aidbox, a hospital's own R4
/// endpoint). Behaviourally identical to <see cref="EpicFhirSourceClient"/> — it adds no overrides, so it keeps
/// the base's transport, paging, retry and default per-resource search parameters exactly as before — and exists
/// so that a generic FHIR source's log lines and error messages name "FHIR" instead of "Epic FHIR", which is what
/// they read as while this source type was registered against the Epic client.
/// <para>
/// The same applies to any vendor re-enabled in <c>FhirSourceClientFactory.DefaultRegistrations</c>: give it a
/// thin subclass like this one rather than pointing it at another vendor's client, or its logs inherit that
/// vendor's name.
/// </para>
/// </summary>
public sealed class GenericFhirSourceClient : FhirSourceConnectorBase
{
    public GenericFhirSourceClient(
        HttpClient httpClient,
        IFhirAccessTokenProvider accessTokenProvider,
        IOptions<EpicFhirClientOptions>? options = null,
        ILogger<GenericFhirSourceClient>? logger = null)
        : base(httpClient, accessTokenProvider, options?.Value, logger)
    {
    }

    protected override string SourceDisplayName => "FHIR";
}
