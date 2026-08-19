using FHIRBridge.Runtime.Application.Abstractions.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Runtime.Infrastructure.Connectors;

/// <summary>
/// eClinicalWorks (Healow) FHIR R4 source connector. Reuses <see cref="FhirSourceConnectorBase"/>'s paginated
/// search/retry/throttle machinery unchanged — unlike athenahealth, no eCW-specific request-level header/query
/// parameter or per-resource-type search requirement has been confirmed against a live sandbox yet (see the
/// ECW_Net proof-of-concept's own "unverified" notes on scope/host shape). This type exists mainly to give eCW its
/// own <see cref="SourceDisplayName"/> for correct log/error messages instead of borrowing Epic's; add overrides
/// here (mirroring AthenahealthFhirSourceClient) if a live run surfaces a real eCW-specific requirement.
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
}
