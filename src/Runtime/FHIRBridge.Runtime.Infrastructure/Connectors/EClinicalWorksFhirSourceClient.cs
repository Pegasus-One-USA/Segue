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
    ///   eCW's Observation categories are the seven the eCW Backend Single Patient proof-of-concept probes and
    ///   that a live Provider EMR run confirmed all answer 200 — NOT the three (vitals/labs/social-history) listed
    ///   here originally. That under-count silently dropped every <c>survey</c> Observation: on the verified
    ///   reference patient, laboratory returned 28 and social-history 2, but <c>survey</c> returned a further 25
    ///   (disability status, PRAPARE scores, the SDOH screening answers) — 30 of 55 extracted instead of all 55,
    ///   with no error, because an absent category value simply never gets requested. <c>vital-signs</c>,
    ///   <c>exam</c>, <c>imaging</c> and <c>sdoh</c> returned nothing for that patient but are accepted, and are
    ///   kept so a patient who does have them is not silently short-changed the same way; <c>sdoh</c> is normally
    ///   redundant (eCW dual-tags those resources <c>survey</c>) and dedupe by resource id absorbs the overlap.
    ///   Still far short of the base's Epic table of 11, whose eCW-invalid values produced wasted/failed requests —
    ///   and enough of them to blow past eCW's ~5-minute access-token lifetime mid-run, since the token is acquired
    ///   ONCE per resource-type fetch (see FhirSourceConnectorBase.SearchAsync) and not refreshed between the
    ///   per-category requests.</item>
    ///   <item><c>CarePlan</c> needs <c>category=assess-plan</c> and <c>CareTeam</c> needs <c>status=active</c> —
    ///   the single US Core value eCW serves for each, taken from the eCW Backend Single Patient proof-of-concept's
    ///   own verified endpoint catalog (RnD/eCWBackendSinglePatientAPI, <c>src/Catalog.php</c>). Neither had any
    ///   entry here before, so a CarePlan search went out with no category at all; the base's Epic CarePlan default
    ///   lists ten Epic-specific category codes eCW does not serve, which is why this table overrides rather than
    ///   extends it.</item>
    ///   <item><c>MedicationRequest</c> and <c>Condition</c> are intentionally ABSENT (no default → one bare
    ///   <c>?patient=</c> search). Unlike Epic, eCW accepts a bare Condition search, and it REJECTS a MedicationRequest
    ///   <c>status</c> filter outright with 400 "does not know how to handle get operation with parameter
    ///   [patient,status]". The base's Epic <c>status</c>/category defaults for these therefore break eCW.</item>
    /// </list>
    /// </summary>
    protected override IReadOnlyDictionary<string, (string ParameterName, string DefaultValue)> DefaultSearchParametersByResourceType { get; } =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Observation"] = ("category", "laboratory,vital-signs,social-history,survey,exam,imaging,sdoh"),
            ["CarePlan"] = ("category", "assess-plan"),
            ["CareTeam"] = ("status", "active"),
            // MedicationRequest is deliberately still ABSENT even though the proof-of-concept sends
            // intent=order (US Core's documented requirement). A live eCW Provider EMR run already confirmed a
            // BARE ?patient= MedicationRequest search succeeds, and that eCW rejects an added `status` filter
            // outright ("does not know how to handle get operation with parameter [patient,status]"). Adding a
            // second filter here would change behaviour that is already verified working, for a requirement eCW
            // evidently does not enforce — so it stays out until a live run shows the bare search failing.
        };

    /// <summary>
    /// eCW has no CarePlan <c>activity-date</c> presence requirement (that is Epic business-rule 59108). Override the
    /// base's Epic-only additional-required table to empty so no Epic-specific parameter is appended to eCW searches.
    /// </summary>
    protected override IReadOnlyDictionary<string, (string ParameterName, string DefaultValue)> AdditionalRequiredParametersByResourceType { get; } =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
}
