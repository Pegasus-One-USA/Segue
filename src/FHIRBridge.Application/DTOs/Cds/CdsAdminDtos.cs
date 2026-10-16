using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Application.DTOs.Cds;

// Admin API shapes for the CDS Hooks screens (api/v1/cds/...). Serialized with the API's defaults (camelCase,
// enums as strings).

/// <param name="HandlerDisplayName">Null when the instance names a handler type that is no longer registered.</param>
/// <param name="Prefetch">The handler's prefetch templates for <paramref name="Hook"/>, as discovery advertises them.</param>
/// <param name="Settings">The effective settings: stored values with the handler's defaults filled in (display).</param>
/// <param name="StoredSettings">
/// Only the values an admin actually set. Send these (not <paramref name="Settings"/>) back on save, so keys left
/// at their default keep following the handler's defaults when those change.
/// </param>
/// <param name="FetchMissingData">Fetch missing prefetch from the EHR's fhirServer (default true).</param>
/// <param name="Fixes">One-click fix settings (<see cref="CdsFixSettings.Disabled"/> when never configured).</param>
/// <param name="FixesEnabled">Fixes master switch — the Services list shows "Changes EHR drafts" when true.</param>
/// <param name="OverrideReasons">The dismiss reasons cards offer (the instance's own list, else the defaults).</param>
/// <param name="OverrideReasonsAreDefault">True when the instance has no list of its own (send null/empty to keep it so).</param>
/// <param name="Links">Links added to every card (empty when none).</param>
/// <param name="SupportedFixTypes">The handler's fix types (empty when the handler is unavailable or offers none).</param>
/// <param name="IsClinicallySignedOff">True while a clinical sign-off is on record (the Services list shows "Reviewed by ...").</param>
/// <param name="SignedOffBy">The reviewing clinician's name as recorded; null when not reviewed.</param>
/// <param name="SignedOffAtUtc">When the sign-off was recorded; null when not reviewed.</param>
/// <param name="SignOffNote">Short note on the review; null when none.</param>
/// <param name="UsesSampleReferenceData">True when the check's rules are the SAMPLE data shipped with Segue.</param>
/// <param name="ContentVersion">
/// Fingerprint of what a sign-off covers (check type, hook, settings, fixes, dismiss reasons, links). Send it back
/// in <see cref="CdsSignOffRequest.ContentVersion"/> so a sign-off is never recorded for content changed meanwhile.
/// </param>
public sealed record CdsServiceInstanceDto(
    Guid Id,
    string ServiceId,
    string Title,
    string HandlerKey,
    string? HandlerDisplayName,
    string Hook,
    string Description,
    bool IsEnabled,
    JsonObject Settings,
    JsonObject StoredSettings,
    string CardSourceLabel,
    string? CardSourceUrl,
    IReadOnlyList<Guid> AllowedIssuerIds,
    IReadOnlyDictionary<string, string> Prefetch,
    DateTime CreatedOnUtc,
    string? CreatedBy,
    DateTime? ModifiedOnUtc,
    string? ModifiedBy,
    bool FetchMissingData,
    CdsFixSettings Fixes,
    bool FixesEnabled,
    IReadOnlyList<CdsOverrideReasonOption> OverrideReasons,
    bool OverrideReasonsAreDefault,
    IReadOnlyList<CdsCardLinkOption> Links,
    IReadOnlyList<CdsFixTypeDescriptor> SupportedFixTypes,
    bool IsClinicallySignedOff = false,
    string? SignedOffBy = null,
    DateTime? SignedOffAtUtc = null,
    string? SignOffNote = null,
    bool UsesSampleReferenceData = false,
    string? ContentVersion = null);

/// <summary>
/// Body of POST api/v1/cds/services/{id}/sign-off: records that a clinician reviewed the service's current rules,
/// fixes, dismiss reasons and links. Advisory: it never blocks switching the service on.
/// </summary>
/// <param name="SignedOffBy">The reviewing clinician's name and role, 1-200 characters (e.g. "Dr A. Patel, CMIO").</param>
/// <param name="Note">Optional, up to 500 characters. Never patient data.</param>
/// <param name="ContentVersion">
/// Required: the <see cref="CdsServiceInstanceDto.ContentVersion"/> the reviewer saw. 409 Conflict when the service
/// changed since (reload, have the clinician review the new content, then sign off again).
/// </param>
public sealed record CdsSignOffRequest(string SignedOffBy, string? Note, string? ContentVersion = null);

/// <summary>Create and update body for a CDS service instance.</summary>
/// <param name="ServiceId">URL slug: 3-100 chars, lower-case letters, digits, single hyphens. Upper case is lowered.</param>
/// <param name="Description">Blank uses the handler type's description.</param>
/// <param name="Settings">Keys from the handler's settings descriptor; unset keys use the defaults.</param>
/// <param name="CardSourceLabel">Blank uses <paramref name="Title"/>.</param>
/// <param name="AllowedIssuerIds">Trusted issuer ids allowed to call; null or empty means any enabled trusted issuer.</param>
/// <param name="FetchMissingData">Default true. The whole object is replaced on update, so always send it.</param>
/// <param name="Fixes">
/// Null means fixes off. Fix type keys must be in the handler's SupportedFixTypes; a switched-on type that
/// RequiresMapping needs at least one mapping, each with the descriptor's required fields, a code system from its
/// CodeSystems and (when the descriptor has Triggers) a trigger from that list or empty for "any".
/// </param>
/// <param name="OverrideReasons">Null or empty means the defaults; otherwise 1-10 reasons, codes 1-50 of [A-Za-z0-9_-], unique.</param>
/// <param name="Links">Up to 10: https URL, type absolute|smart, appContext / autolaunchable on smart links only.</param>
public sealed record SaveCdsServiceInstanceRequest(
    string ServiceId,
    string Title,
    string HandlerKey,
    string Hook,
    string? Description,
    bool IsEnabled,
    JsonObject? Settings,
    string? CardSourceLabel,
    string? CardSourceUrl,
    IReadOnlyList<Guid>? AllowedIssuerIds,
    bool FetchMissingData = true,
    CdsFixSettings? Fixes = null,
    IReadOnlyList<CdsOverrideReasonOption>? OverrideReasons = null,
    IReadOnlyList<CdsCardLinkOption>? Links = null);

public sealed record CdsTrustedIssuerDto(
    Guid Id,
    string Name,
    string Vendor,
    string Issuer,
    string? JwksUrl,
    string? JwksJson,
    string? Audience,
    bool IsEnabled,
    DateTime CreatedOnUtc,
    string? CreatedBy,
    DateTime? ModifiedOnUtc,
    string? ModifiedBy,
    string? ExpectedSubject,
    string? FhirBaseUrl,
    string EffectiveFhirBaseUrl,
    bool AllowCallback);

/// <summary>Create and update body for a trusted issuer. At least one of JwksUrl / JwksJson is required.</summary>
/// <param name="Audience">Blank means the JWT aud must be the exact service URL.</param>
/// <param name="ExpectedSubject">The JWT sub to require (Epic: the CDS client id). Blank means sub is not checked.</param>
/// <param name="FhirBaseUrl">http(s) FHIR base (no query or fragment) the request's fhirServer must equal for a callback; http is called only with Cds:AllowInsecureCallback. Blank means use Issuer (Epic iss = FHIR base).</param>
/// <param name="AllowCallback">Let Segue fetch missing prefetch from this EHR's fhirServer. Default true.</param>
public sealed record SaveCdsTrustedIssuerRequest(
    string Name,
    string Vendor,
    string Issuer,
    string? JwksUrl,
    string? JwksJson,
    string? Audience,
    bool IsEnabled,
    string? ExpectedSubject = null,
    string? FhirBaseUrl = null,
    bool AllowCallback = true);

/// <summary>One built-in handler type, for the "new service" picker and the settings form.</summary>
/// <param name="PrefetchByHook">Hook → (prefetch key → FHIR query template).</param>
/// <param name="UsesSampleReferenceData">True when the check's rules are the SAMPLE data shipped with Segue (the portal warns).</param>
public sealed record CdsHandlerDto(
    string Key,
    string DisplayName,
    string Description,
    IReadOnlyList<string> SupportedHooks,
    IReadOnlyList<CdsSettingDescriptor> Settings,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> PrefetchByHook,
    IReadOnlyList<CdsFixTypeDescriptor> FixTypes,
    bool UsesSampleReferenceData = false);

/// <summary>The URLs to register with an EHR.</summary>
/// <param name="PublicBaseUrl">Base with no trailing slash.</param>
/// <param name="IsConfigured">False when it fell back to this request's origin (set <paramref name="ConfigurationKey"/> behind a proxy).</param>
/// <param name="ConfigurationKey">Always "Cds:PublicBaseUrl".</param>
public sealed record CdsDiscoveryInfoDto(
    string PublicBaseUrl,
    bool IsConfigured,
    string ConfigurationKey,
    string DiscoveryUrl,
    IReadOnlyList<CdsDiscoveryInfoServiceDto> Services);

/// <param name="ServiceUrl">Also the default JWT audience for calls to this service.</param>
public sealed record CdsDiscoveryInfoServiceDto(
    Guid Id,
    string ServiceId,
    string Title,
    string Hook,
    bool IsEnabled,
    string ServiceUrl,
    string FeedbackUrl);

/// <summary>One CDS call (no PHI).</summary>
/// <param name="ServiceTitle">The instance's current title, when it still exists.</param>
/// <param name="TrustedIssuerName">The trusted issuer's current name, when matched and it still exists.</param>
/// <param name="TriggerAction">Epic bpa-trigger-action (e.g. "5", "60"); null for other clients.</param>
/// <param name="CallbackResourceCount">Resources fetched from the EHR's fhirServer; null when no callback was made.</param>
/// <param name="CallbackMs">Callback time; null when no callback was made.</param>
/// <param name="SuggestionsOffered">Invoke: fixes offered across the cards.</param>
/// <param name="SuggestionsAccepted">Feedback: fixes accepted.</param>
/// <param name="OverrideReasonCodes">Feedback: distinct override reason codes (codes only).</param>
/// <param name="OverrideReasonCounts">Feedback: how many items of the call (Epic batches) carried each reason code.</param>
/// <param name="SystemActionCount">Invoke: systemActions returned (coverage requirements only).</param>
/// <param name="PayerId">Coverage requirements: the matched payer; null when none matched or not a CRD call.</param>
/// <param name="PayerName">Coverage requirements: the payer's name at the time of the call.</param>
/// <param name="CoverageDecisions">Coverage requirements: one "covered/pa-needed/doc-needed" per order ("-" = not stated).</param>
/// <param name="RelayUsed">Coverage requirements: true when relayed to the payer's CRD service; null for other calls.</param>
/// <param name="RelayMs">Coverage requirements: time spent on the relay.</param>
/// <param name="RelayOutcome">Coverage requirements: "ok", or why the relay failed (e.g. "timed out; local rules used").</param>
/// <param name="CoverageAssertionCount">Coverage requirements: coverage-information extensions (coverage-assertion-ids) returned.</param>
public sealed record CdsCallLogDto(
    Guid Id,
    DateTime OccurredOnUtc,
    CdsCallKind Kind,
    string ServiceId,
    Guid? ServiceInstanceId,
    string? ServiceTitle,
    string? HandlerKey,
    string? Hook,
    string? HookInstance,
    string? Issuer,
    Guid? TrustedIssuerId,
    string? TrustedIssuerName,
    CdsCallOutcome Outcome,
    int HttpStatus,
    int CardCount,
    long DurationMs,
    string? Detail,
    string? TriggerAction,
    string? EpicVersion,
    string? CriteriaId,
    string? ImplementationVersion,
    int? CallbackResourceCount,
    long? CallbackMs,
    int SuggestionsOffered,
    int SuggestionsAccepted,
    IReadOnlyList<string> OverrideReasonCodes,
    IReadOnlyList<CdsReasonCountDto> OverrideReasonCounts,
    int SystemActionCount = 0,
    Guid? PayerId = null,
    string? PayerName = null,
    IReadOnlyList<string>? CoverageDecisions = null,
    bool? RelayUsed = null,
    long? RelayMs = null,
    string? RelayOutcome = null,
    int? CoverageAssertionCount = null);

/// <summary>One override reason code and how many feedback items of a call carried it.</summary>
public sealed record CdsReasonCountDto(string Code, int Count);

/// <summary>
/// The stored form of a feedback call's reason codes: "code" or "code:count", comma-separated (codes are short
/// tokens with no ':' or ','), in first-seen order, cut at whole entries to fit the column.
/// </summary>
public static class CdsReasonCodeCounts
{
    public static string? Format(IEnumerable<string> codes, int maxLength)
    {
        var counts = new List<(string Code, int Count)>();
        foreach (var code in codes)
        {
            var index = counts.FindIndex(entry => string.Equals(entry.Code, code, StringComparison.Ordinal));
            if (index < 0)
            {
                counts.Add((code, 1));
            }
            else
            {
                counts[index] = (code, counts[index].Count + 1);
            }
        }

        var joined = string.Empty;
        foreach (var (code, count) in counts)
        {
            var entry = count > 1 ? $"{code}:{count.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : code;
            var next = joined.Length == 0 ? entry : $"{joined},{entry}";
            if (next.Length > maxLength)
            {
                break;
            }

            joined = next;
        }

        return joined.Length == 0 ? null : joined;
    }

    public static IReadOnlyList<CdsReasonCountDto> Parse(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return [];
        }

        var result = new List<CdsReasonCountDto>();
        foreach (var entry in stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = entry.LastIndexOf(':');
            if (colon > 0
                && int.TryParse(entry[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count)
                && count > 0)
            {
                result.Add(new CdsReasonCountDto(entry[..colon], count));
            }
            else
            {
                result.Add(new CdsReasonCountDto(entry, 1));
            }
        }

        return result;
    }
}

public sealed record CdsActivityPageDto(
    IReadOnlyList<CdsCallLogDto> Items,
    int TotalCount,
    int Page,
    int PageSize);
