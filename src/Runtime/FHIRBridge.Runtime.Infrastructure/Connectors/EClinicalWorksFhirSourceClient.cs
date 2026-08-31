using System;
using System.Collections.Generic;
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

    /// <summary>
    /// eCW-specific per-resource search defaults, overriding the base's Epic table. Confirmed against the live eCW
    /// sandbox during a real Provider EMR launch run:
    /// <list type="bullet">
    ///   <item>eCW's US Core <c>Observation</c> search requires a <c>category</c> (a bare patient search is rejected);
    ///   eCW serves the three USCDI categories below (vitals, labs, social-history/smoking). The base's Epic table
    ///   lists 11 categories eCW does not have, which only produce wasted/failed per-category requests — and enough of
    ///   them to blow past eCW's ~5-minute access-token lifetime mid-run.</item>
    ///   <item><c>MedicationRequest</c> and <c>Condition</c> are intentionally ABSENT (no default → one bare
    ///   <c>?patient=</c> search). Unlike Epic, eCW accepts a bare Condition search, and it REJECTS a MedicationRequest
    ///   <c>status</c> filter outright with 400 "does not know how to handle get operation with parameter
    ///   [patient,status]". The base's Epic <c>status</c>/category defaults for these therefore break eCW.</item>
    /// </list>
    /// </summary>
    protected override IReadOnlyDictionary<string, (string ParameterName, string DefaultValue)> DefaultSearchParametersByResourceType { get; } =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Observation"] = ("category", "vital-signs,laboratory,social-history"),
        };

    /// <summary>
    /// eCW has no CarePlan <c>activity-date</c> presence requirement (that is Epic business-rule 59108). Override the
    /// base's Epic-only additional-required table to empty so no Epic-specific parameter is appended to eCW searches.
    /// </summary>
    protected override IReadOnlyDictionary<string, (string ParameterName, string DefaultValue)> AdditionalRequiredParametersByResourceType { get; } =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
}
