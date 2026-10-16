using System.Text.Json.Nodes;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Application.Abstractions.Cds;

/// <summary>The instance a call reached, as a handler sees it.</summary>
/// <param name="Fixes">The instance's one-click fix settings; null or disabled means cards carry no suggestions.</param>
/// <param name="OverrideReasons">The instance's dismiss reasons; null means <see cref="CdsOverrideReasonOption.Defaults"/>.</param>
/// <param name="Links">Links added to the instance's cards; null means none.</param>
public sealed record CdsInstanceSnapshot(
    Guid Id,
    string ServiceId,
    string Title,
    string HandlerKey,
    string Hook,
    string CardSourceLabel,
    string? CardSourceUrl,
    CdsFixSettings? Fixes = null,
    IReadOnlyList<CdsOverrideReasonOption>? OverrideReasons = null,
    IReadOnlyList<CdsCardLinkOption>? Links = null)
{
    public static CdsInstanceSnapshot From(CdsServiceInstance instance) =>
        new(
            instance.Id,
            instance.ServiceId,
            instance.Title,
            instance.HandlerKey,
            instance.Hook,
            instance.CardSourceLabel,
            instance.CardSourceUrl,
            instance.GetFixes(),
            instance.GetOverrideReasons(),
            instance.GetLinks());
}

/// <summary>Everything a handler gets for one evaluation.</summary>
public sealed class CdsHandlerContext
{
    public CdsHandlerContext(CdsInstanceSnapshot instance, CdsRequest request, CdsSettingsValues settings)
    {
        Instance = instance;
        Request = request;
        Settings = settings;
    }

    public CdsInstanceSnapshot Instance { get; }

    /// <summary>
    /// The CDS request. Holds PHI: read it, never log or keep it. When the instance fetches missing data, the
    /// evaluator has already merged the callback results into <c>Request.Prefetch</c>.
    /// </summary>
    public CdsRequest Request { get; }

    /// <summary>The instance's settings with the handler's defaults filled in.</summary>
    public CdsSettingsValues Settings { get; }

    /// <summary>The dismiss reasons every card of this instance should offer (the instance's list, else the defaults).</summary>
    public IReadOnlyList<CdsCoding> OverrideReasons =>
        (Instance.OverrideReasons is { Count: > 0 } own ? own : CdsOverrideReasonOption.Defaults)
        .Select(reason => new CdsCoding { System = CdsOverrideReasonOption.System, Code = reason.Code, Display = reason.Display })
        .ToArray();

    /// <summary>The instance's configured links, as card links (empty when none).</summary>
    public IReadOnlyList<CdsLink> Links =>
        (Instance.Links ?? [])
        .Select(link => new CdsLink
        {
            Label = link.Label,
            Url = link.Url,
            Type = link.Type,
            AppContext = link.Type == CdsCardLinkTypes.Smart ? link.AppContext : null,
            Autolaunchable = link.Type == CdsCardLinkTypes.Smart && link.Autolaunchable ? true : null,
        })
        .ToArray();

    /// <summary>
    /// A card carrying this instance's source label/url and a fresh uuid. The summary is cut to the spec's 140
    /// characters; <paramref name="indicator"/> falls back to "info" if it is not a CDS Hooks indicator.
    /// <paramref name="topic"/> (see <see cref="CdsTopic.Create"/>) must be the rule's static code. Null suggestions
    /// (e.g. from <see cref="CreateDeleteSuggestion"/> on a switched-off fix) are dropped; when any remain the card
    /// gets <c>selectionBehavior: "any"</c>.
    /// </summary>
    public CdsCard CreateCard(
        string summary,
        string indicator,
        string? detail = null,
        IReadOnlyList<CdsLink>? links = null,
        IReadOnlyList<CdsCoding>? overrideReasons = null,
        CdsCoding? topic = null,
        IEnumerable<CdsSuggestion?>? suggestions = null)
    {
        var trimmed = summary.Trim();
        var offered = (suggestions ?? []).OfType<CdsSuggestion>().ToArray();
        // Epic pre-selects every recommended suggestion, so only the first one of the card stays recommended.
        foreach (var extra in offered.Where(s => s.IsRecommended == true).Skip(1))
        {
            extra.IsRecommended = null;
        }

        return new CdsCard
        {
            Uuid = Guid.NewGuid().ToString(),
            Summary = CdsCard.TruncateSummary(trimmed),
            Indicator = CdsIndicator.All.Contains(indicator) ? indicator : CdsIndicator.Info,
            Detail = detail,
            Source = new CdsSource { Label = Instance.CardSourceLabel, Url = Instance.CardSourceUrl, Topic = topic },
            Links = links is { Count: > 0 } ? links : null,
            OverrideReasons = overrideReasons is { Count: > 0 } ? overrideReasons : null,
            Suggestions = offered.Length > 0 ? offered : null,
            SelectionBehavior = offered.Length > 0 ? CdsSelectionBehavior.Any : null,
        };
    }

    /// <summary>True when the instance's master fixes switch and this fix type's switch are both on.</summary>
    public bool IsFixEnabled(string fixTypeKey) => Instance.Fixes?.EnabledFixType(fixTypeKey) is not null;

    /// <summary>
    /// The code mapping a switched-on fix uses for <paramref name="trigger"/>: the mapping for that trigger, else the
    /// one with no trigger; null when the fix is off or has no usable mapping.
    /// </summary>
    public CdsFixCodeMapping? FindFixMapping(string fixTypeKey, string? trigger = null)
    {
        var fix = Instance.Fixes?.EnabledFixType(fixTypeKey);
        if (fix is null)
        {
            return null;
        }

        return (trigger is null ? null : fix.Mappings.FirstOrDefault(m => string.Equals(m.Trigger, trigger, StringComparison.Ordinal)))
               ?? fix.Mappings.FirstOrDefault(m => m.Trigger is null);
    }

    /// <summary>A delete suggestion for a draft order ("MedicationRequest/abc"), or null when the fix type is off.</summary>
    public CdsSuggestion? CreateDeleteSuggestion(string fixTypeKey, string label, string draftOrderReference, string description) =>
        IsFixEnabled(fixTypeKey)
            ? Suggestion(fixTypeKey, label, new CdsAction { Type = CdsActionTypes.Delete, Description = description, ResourceId = draftOrderReference })
            : null;

    /// <summary>
    /// A create suggestion (resource built with <see cref="CdsFixResources"/>), or null when the fix type is off.
    /// </summary>
    public CdsSuggestion? CreateCreateSuggestion(string fixTypeKey, string label, JsonObject resource, string description) =>
        IsFixEnabled(fixTypeKey)
            ? Suggestion(fixTypeKey, label, new CdsAction { Type = CdsActionTypes.Create, Description = description, Resource = resource })
            : null;

    private CdsSuggestion Suggestion(string fixTypeKey, string label, CdsAction action) => new()
    {
        Label = label,
        Uuid = Guid.NewGuid().ToString(),
        IsRecommended = string.Equals(Instance.Fixes?.RecommendedFixType, fixTypeKey, StringComparison.Ordinal) ? true : null,
        Actions = [action],
    };
}

/// <summary>
/// Builds the resources a create suggestion carries, in the shape Epic accepts: orders with status "draft" and
/// intent "proposal", the mapped code, and the request's patient. Epic places the order with its own defaults.
/// </summary>
public static class CdsFixResources
{
    /// <summary>The category every created MedicationRequest carries (FHIR medicationrequest-category "outpatient").</summary>
    public static readonly (string System, string Code, string Display) MedicationRequestCategory =
        ("http://terminology.hl7.org/CodeSystem/medicationrequest-category", "outpatient", "Outpatient");

    /// <summary>
    /// The category every created ServiceRequest carries (SNOMED CT 108252007 "Laboratory procedure"): the only
    /// ServiceRequest fix is the care-gap test order.
    /// </summary>
    public static readonly (string System, string Code, string Display) ServiceRequestCategory =
        ("http://snomed.info/sct", "108252007", "Laboratory procedure");

    public static JsonObject MedicationRequest(CdsFixCodeMapping mapping, string patientId) => new()
    {
        ["resourceType"] = "MedicationRequest",
        ["status"] = "draft",
        ["intent"] = "proposal",
        ["category"] = Category(MedicationRequestCategory),
        ["medicationCodeableConcept"] = Concept(mapping),
        ["subject"] = new JsonObject { ["reference"] = $"Patient/{patientId}" },
    };

    public static JsonObject ServiceRequest(CdsFixCodeMapping mapping, string patientId) => new()
    {
        ["resourceType"] = "ServiceRequest",
        ["status"] = "draft",
        ["intent"] = "proposal",
        ["category"] = Category(ServiceRequestCategory),
        ["code"] = Concept(mapping),
        ["subject"] = new JsonObject { ["reference"] = $"Patient/{patientId}" },
    };

    private static JsonArray Category((string System, string Code, string Display) category) =>
        new(new JsonObject
        {
            ["coding"] = new JsonArray(new JsonObject
            {
                ["system"] = category.System,
                ["code"] = category.Code,
                ["display"] = category.Display,
            }),
        });

    /// <param name="category">"problem-list-item" or "encounter-diagnosis".</param>
    /// <param name="encounterId">Required for encounter-diagnosis (the request's context.encounterId).</param>
    public static JsonObject Condition(CdsFixCodeMapping mapping, string patientId, string category, string? encounterId = null)
    {
        var condition = new JsonObject
        {
            ["resourceType"] = "Condition",
            ["category"] = new JsonArray(new JsonObject
            {
                ["coding"] = new JsonArray(new JsonObject
                {
                    ["system"] = "http://terminology.hl7.org/CodeSystem/condition-category",
                    ["code"] = category,
                }),
            }),
            ["code"] = Concept(mapping),
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{patientId}" },
        };

        if (!string.IsNullOrWhiteSpace(encounterId))
        {
            condition["encounter"] = new JsonObject { ["reference"] = $"Encounter/{encounterId}" };
        }

        return condition;
    }

    private static JsonObject Concept(CdsFixCodeMapping mapping)
    {
        var coding = new JsonObject { ["system"] = mapping.System, ["code"] = mapping.Code };
        if (mapping.Display is not null)
        {
            coding["display"] = mapping.Display;
        }

        var concept = new JsonObject { ["coding"] = new JsonArray(coding) };
        if (mapping.Display is not null)
        {
            concept["text"] = mapping.Display;
        }

        return concept;
    }
}

/// <summary>
/// The rules every suggestion must meet before a card leaves Segue (the evaluator drops a suggestion that fails):
/// a label, a uuid, exactly one action; delete names a draft order present in <c>context.draftOrders</c>; create
/// carries a resource, and an order (MedicationRequest / ServiceRequest / ProcedureRequest) has status "draft".
/// </summary>
public static class CdsSuggestionRules
{
    private static readonly HashSet<string> OrderTypes = new(StringComparer.Ordinal)
    {
        "MedicationRequest", "ServiceRequest", "ProcedureRequest",
    };

    /// <param name="draftOrderReferences">"Type/id" of every resource in the request's context.draftOrders.</param>
    /// <returns>Null when valid; otherwise a short non-PHI reason.</returns>
    public static string? Validate(CdsSuggestion suggestion, IReadOnlySet<string> draftOrderReferences)
    {
        if (string.IsNullOrWhiteSpace(suggestion.Label))
        {
            return "A suggestion has no label.";
        }

        if (string.IsNullOrWhiteSpace(suggestion.Uuid))
        {
            return "A suggestion has no uuid.";
        }

        if (suggestion.Actions is not { Count: 1 } || suggestion.Actions[0] is not { } action)
        {
            return "A suggestion must have exactly one action.";
        }

        if (action.Type == CdsActionTypes.Delete)
        {
            return action.ResourceId is { } id && draftOrderReferences.Contains(id)
                ? null
                : "A delete suggestion must name a draft order from the request.";
        }

        if (action.Type != CdsActionTypes.Create)
        {
            return "Only create and delete suggestions are sent.";
        }

        if (action.Resource is not { } resource || Text(resource["resourceType"]) is not { } type)
        {
            return "A create suggestion has no resource.";
        }

        return OrderTypes.Contains(type) && Text(resource["status"]) != "draft"
            ? "A created order must have status 'draft'."
            : null;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
