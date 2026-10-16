using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace FHIRBridge.Application.DTOs.Cds;

// HL7 CDS Hooks 2.0 wire format (https://cds-hooks.hl7.org/2.0/). Property names are pinned with
// [JsonPropertyName] so the payload matches the spec whatever serializer options the host uses, and optional
// members are left out when null. Context and prefetch stay as raw JSON: they are hook- and EHR-specific, and
// they hold PHI — nothing in them is ever logged or stored.

/// <summary>The body of POST /cds-services/{id}.</summary>
public sealed class CdsRequest
{
    [JsonPropertyName("hook")]
    public string Hook { get; set; } = string.Empty;

    /// <summary>A UUID the EHR generates for this firing of the hook.</summary>
    [JsonPropertyName("hookInstance")]
    public string HookInstance { get; set; } = string.Empty;

    [JsonPropertyName("fhirServer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FhirServer { get; set; }

    [JsonPropertyName("fhirAuthorization")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CdsFhirAuthorization? FhirAuthorization { get; set; }

    [JsonPropertyName("context")]
    public JsonObject? Context { get; set; }

    /// <summary>Prefetch key → FHIR resource (or Bundle), or null when the EHR could not fetch it.</summary>
    [JsonPropertyName("prefetch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Prefetch { get; set; }

    /// <summary>
    /// Vendor extensions (Epic: <c>com.epic.cdshooks.request.*</c>, see <see cref="CdsEpicExtensions"/>). Only the
    /// version / trigger keys are read (by <see cref="CdsRequestMetadata.Read"/>); never logged as a whole.
    /// </summary>
    [JsonPropertyName("extension")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Extension { get; set; }
}

/// <summary>An OAuth 2.0 bearer token the CDS service may use against fhirServer. Never logged.</summary>
public sealed class CdsFhirAuthorization
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "Bearer";

    /// <summary>Nullable and read from a string too: v1 never uses it, so an odd value must not sink the request.</summary>
    [JsonPropertyName("expires_in")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? ExpiresIn { get; set; }

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = string.Empty;

    [JsonPropertyName("patient")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Patient { get; set; }
}

/// <summary>The body Segue returns from POST /cds-services/{id}.</summary>
public sealed class CdsResponse
{
    [JsonPropertyName("cards")]
    public IReadOnlyList<CdsCard> Cards { get; set; } = [];
}

/// <summary>
/// One advice card. v1 cards carry no suggestions or actions. <see cref="Summary"/> must be under 140 characters
/// (the spec's "&lt;140-character summary"); <see cref="Indicator"/> is one of <see cref="CdsIndicator"/>.
/// </summary>
public sealed class CdsCard
{
    public const int SummaryMaxLength = 139;

    /// <summary>
    /// Cuts a summary to <see cref="SummaryMaxLength"/> characters, ending in "…", without splitting a surrogate pair.
    /// </summary>
    public static string TruncateSummary(string summary)
    {
        if (summary.Length <= SummaryMaxLength)
        {
            return summary;
        }

        var cut = SummaryMaxLength - 1;
        if (char.IsHighSurrogate(summary[cut - 1]))
        {
            cut--;
        }

        return summary[..cut] + "…";
    }

    [JsonPropertyName("uuid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Uuid { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    /// <summary>GitHub-flavored markdown.</summary>
    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }

    [JsonPropertyName("indicator")]
    public string Indicator { get; set; } = CdsIndicator.Info;

    [JsonPropertyName("source")]
    public CdsSource Source { get; set; } = new();

    /// <summary>
    /// One-click fixes. Epic: one action per suggestion, action type create or delete only, and a uuid on every
    /// suggestion so acceptance comes back in feedback.
    /// </summary>
    [JsonPropertyName("suggestions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CdsSuggestion>? Suggestions { get; set; }

    /// <summary><see cref="CdsSelectionBehavior.Any"/> whenever <see cref="Suggestions"/> is set (Epic supports "any" only).</summary>
    [JsonPropertyName("selectionBehavior")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectionBehavior { get; set; }

    [JsonPropertyName("overrideReasons")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CdsCoding>? OverrideReasons { get; set; }

    [JsonPropertyName("links")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CdsLink>? Links { get; set; }

    /// <summary>
    /// Card extensions, e.g. <c>{"com.epic.cdshooks.card.detail.content-type": "text/plain"}</c>
    /// (<see cref="CdsEpicExtensions.CardDetailContentType"/>). Absent means markdown detail.
    /// </summary>
    [JsonPropertyName("extension")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Extension { get; set; }
}

/// <summary>The allowed values of <see cref="CdsCard.SelectionBehavior"/> Segue sends.</summary>
public static class CdsSelectionBehavior
{
    public const string Any = "any";
}

/// <summary>A one-click fix offered on a card (CDS Hooks 2.0 Suggestion).</summary>
public sealed class CdsSuggestion
{
    /// <summary>Required by the spec; Epic does not display it.</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>Needed to receive acceptance in feedback (acceptedSuggestions[].id).</summary>
    [JsonPropertyName("uuid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Uuid { get; set; }

    /// <summary>True pre-selects the suggestion in Epic.</summary>
    [JsonPropertyName("isRecommended")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsRecommended { get; set; }

    /// <summary>Exactly one action (Epic supports one per suggestion).</summary>
    [JsonPropertyName("actions")]
    public IReadOnlyList<CdsAction> Actions { get; set; } = [];
}

/// <summary>One change a suggestion makes in the EHR (CDS Hooks 2.0 Action). Segue uses create and delete only.</summary>
public sealed class CdsAction
{
    /// <summary><see cref="CdsActionTypes.Create"/> or <see cref="CdsActionTypes.Delete"/>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = CdsActionTypes.Create;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>create: the FHIR resource to create (status "draft"; e.g. MedicationRequest, ServiceRequest, Condition).</summary>
    [JsonPropertyName("resource")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Resource { get; set; }

    /// <summary>delete: the draft order to remove, as "Type/id" (e.g. "MedicationRequest/abc"), taken from draftOrders.</summary>
    [JsonPropertyName("resourceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResourceId { get; set; }
}

/// <summary>The action types Segue sends (Epic supports create and delete only; no update, no systemActions).</summary>
public static class CdsActionTypes
{
    public const string Create = "create";
    public const string Delete = "delete";

    public static readonly IReadOnlyList<string> All = [Create, Delete];
}

/// <summary>The allowed values of <see cref="CdsCard.Indicator"/>.</summary>
public static class CdsIndicator
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Critical = "critical";

    public static readonly IReadOnlyList<string> All = [Info, Warning, Critical];
}

public sealed class CdsSource
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonPropertyName("icon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Icon { get; set; }

    [JsonPropertyName("topic")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CdsCoding? Topic { get; set; }
}

public sealed class CdsCoding
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("system")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? System { get; set; }

    [JsonPropertyName("display")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Display { get; set; }
}

public sealed class CdsLink
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>"absolute" or "smart".</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "absolute";

    /// <summary>smart links only.</summary>
    [JsonPropertyName("appContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AppContext { get; set; }

    /// <summary>smart links only; true lets Epic (Aug 2023+) launch the app without a click. Left out when not set.</summary>
    [JsonPropertyName("autolaunchable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Autolaunchable { get; set; }
}

/// <summary>The body of GET /cds-services.</summary>
public sealed class CdsDiscoveryResponse
{
    [JsonPropertyName("services")]
    public IReadOnlyList<CdsServiceDefinition> Services { get; set; } = [];
}

/// <summary>One entry of the discovery response.</summary>
public sealed class CdsServiceDefinition
{
    [JsonPropertyName("hook")]
    public string Hook { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Prefetch key → FHIR query template (e.g. "Patient/{{context.patientId}}").</summary>
    [JsonPropertyName("prefetch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Prefetch { get; set; }

    [JsonPropertyName("usageRequirements")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UsageRequirements { get; set; }
}

/// <summary>The body of POST /cds-services/{id}/feedback.</summary>
public sealed class CdsFeedbackRequest
{
    [JsonPropertyName("feedback")]
    public IReadOnlyList<CdsFeedbackItem> Feedback { get; set; } = [];
}

public sealed class CdsFeedbackItem
{
    /// <summary>The uuid of the card this feedback is about.</summary>
    [JsonPropertyName("card")]
    public string Card { get; set; } = string.Empty;

    /// <summary>"accepted" or "overridden".</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = string.Empty;

    [JsonPropertyName("acceptedSuggestions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CdsAcceptedSuggestion>? AcceptedSuggestions { get; set; }

    /// <summary>Spec 2.0 member (singular). May carry a free-text userComment; never logged or stored.</summary>
    [JsonPropertyName("overrideReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CdsOverrideReason? OverrideReason { get; set; }

    /// <summary>Epic's member name (plural, same shape). May carry a userComment; never logged or stored.</summary>
    [JsonPropertyName("overrideReasons")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CdsOverrideReason? OverrideReasons { get; set; }

    /// <summary>Whichever of <see cref="OverrideReason"/> / <see cref="OverrideReasons"/> was sent (singular first).</summary>
    [JsonIgnore]
    public CdsOverrideReason? EffectiveOverrideReason => OverrideReason ?? OverrideReasons;

    [JsonPropertyName("outcomeTimestamp")]
    public string OutcomeTimestamp { get; set; } = string.Empty;
}

public sealed class CdsAcceptedSuggestion
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
}

public sealed class CdsOverrideReason
{
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CdsCoding? Reason { get; set; }

    /// <summary>Free text from the clinician (PHI risk): never logged or stored.</summary>
    [JsonPropertyName("userComment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserComment { get; set; }

    /// <summary>
    /// Epic's coded reason when the health system did not map one of Segue's codes:
    /// <c>com.epic.cdshooks.feedback.overrideReason.reasonCategory</c> (a code, e.g. "45"), <c>...reasonDisplay</c>
    /// and <c>...reasonTitle</c>. Only the category code is recorded.
    /// </summary>
    [JsonPropertyName("extension")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Extension { get; set; }

    /// <summary>
    /// The code to record for this override: <c>reason.code</c> when it is a short code, else Epic's reasonCategory
    /// extension; null when neither is a short code (letters, digits, '.', '-', '_', at most 40 characters).
    /// </summary>
    public string? RecordableCode()
    {
        if (CdsRequestMetadata.SafeToken(Reason?.Code, 40) is { } reasonCode)
        {
            return reasonCode;
        }

        return Extension?[CdsEpicExtensions.FeedbackReasonCategory] is JsonValue category
               && category.GetValueKind() is System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Number
            ? CdsRequestMetadata.SafeToken(category.ToString(), 40)
            : null;
    }
}
