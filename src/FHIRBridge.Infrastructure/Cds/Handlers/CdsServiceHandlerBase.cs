using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Infrastructure.Cds.Handlers;

/// <summary>
/// Shared plumbing for the built-in CDS handler types: reading draft orders and prefetch, building synthetic
/// sample requests, and the guarantee that a handler never throws on unexpected input — anything it cannot read
/// becomes "no cards" with a short, non-PHI degraded reason. Handlers are singletons and hold no state.
/// </summary>
public abstract class CdsServiceHandlerBase : ICdsServiceHandler
{
    protected const string SamplePatientId = "sample-patient-1";
    /// <summary>Epic sends userId as "PractitionerRole/&lt;id&gt;" (or "Practitioner/&lt;id&gt;").</summary>
    protected const string SampleUserId = "PractitionerRole/sample-practitioner-role-1";
    protected const string SampleEncounterId = "sample-encounter-1";

    protected static readonly IReadOnlyList<CdsSettingOption> IndicatorOptions =
    [
        new(CdsIndicator.Info, "Info"),
        new(CdsIndicator.Warning, "Warning"),
        new(CdsIndicator.Critical, "Critical"),
    ];


    public abstract string Key { get; }

    /// <summary>Every built-in rule set reads <see cref="CdsSampleReferenceData"/>, which needs clinical review.</summary>
    public virtual bool UsesSampleReferenceData => true;

    /// <summary>Only the coverage requirements handler returns systemActions.</summary>
    public virtual bool ReturnsSystemActions => false;

    public abstract string DisplayName { get; }

    public abstract string Description { get; }

    public abstract IReadOnlyList<string> SupportedHooks { get; }

    public abstract IReadOnlyList<CdsSettingDescriptor> Settings { get; }

    public abstract IReadOnlyDictionary<string, string> GetPrefetchTemplates(string hook);

    /// <summary>By default the prefetch templates themselves are the queries run when a key is missing.</summary>
    public virtual IReadOnlyDictionary<string, string> GetFallbackQueries(string hook) => GetPrefetchTemplates(hook);

    /// <summary>No fixes unless a handler declares some.</summary>
    public virtual IReadOnlyList<CdsFixTypeDescriptor> SupportedFixTypes => [];

    /// <summary>The drug classes of the sample data, as fix-mapping triggers (value = class key).</summary>
    protected static IReadOnlyList<CdsSettingOption> DrugClassTriggers =>
        CdsSampleReferenceData.DrugClasses.Select(c => new CdsSettingOption(c.Key, c.DisplayName)).ToArray();

    /// <summary>Ingredient names as fix-mapping triggers (value = lower-case ingredient name).</summary>
    protected static IReadOnlyList<CdsSettingOption> IngredientTriggers(IEnumerable<string> names) =>
        names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(n => new CdsSettingOption(n, n)).ToArray();

    public abstract CdsRequest BuildSampleRequest(string hook);

    public Task<CdsHandlerResult> EvaluateAsync(CdsHandlerContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(Evaluate(context, cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Only the exception type: a message could quote a value from the request.
            return Task.FromResult(new CdsHandlerResult([], $"The request data could not be read ({exception.GetType().Name})."));
        }
    }

    /// <summary>The rules. Runs synchronously over the in-memory request; should check the token in long loops.</summary>
    protected abstract CdsHandlerResult Evaluate(CdsHandlerContext context, CancellationToken cancellationToken);

    protected static CdsSettingDescriptor IndicatorSetting(string key, string label, string defaultValue, string help) =>
        new(key, label, CdsSettingTypes.Select, JsonValue.Create(defaultValue), help, IndicatorOptions);

    protected static JsonNode? Prefetch(CdsHandlerContext context, string key) => context.Request.Prefetch?[key];

    /// <summary>
    /// The orders to check: on order-select, only the newly selected ones (context.selections, "Type/id") when
    /// given; otherwise every resource in context.draftOrders.
    /// </summary>
    protected static IReadOnlyList<JsonObject> OrdersToCheck(CdsHandlerContext context)
    {
        var drafts = DraftOrders(context);
        if (context.Request.Hook != CdsHookNames.OrderSelect
            || context.Request.Context?["selections"] is not JsonArray selections)
        {
            return drafts;
        }

        var selected = selections
            .Select(CdsFhirReader.Text)
            .Where(reference => reference is not null)
            .ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0)
        {
            return drafts;
        }

        var matching = drafts
            .Where(order => selected.Contains($"{CdsFhirReader.ResourceType(order)}/{CdsFhirReader.Text(order["id"])}"))
            .ToArray();
        return matching.Length > 0 ? matching : drafts;
    }

    /// <summary>
    /// The orders in context.draftOrders (MedicationRequest, ServiceRequest, STU3 ProcedureRequest, ...), without the
    /// Medication entries Epic includes beside them for medicationReference.
    /// </summary>
    protected static IReadOnlyList<JsonObject> DraftOrders(CdsHandlerContext context) =>
        CdsFhirReader.Resources(context.Request.Context?["draftOrders"])
            .Where(resource => CdsFhirReader.IsOrderType(CdsFhirReader.ResourceType(resource)))
            .ToArray();

    /// <summary>
    /// Every Medication the request carries as a Bundle entry (draftOrders and prefetch), for resolving a
    /// MedicationRequest's medicationReference (<see cref="CdsFhirReader.MedicationConcept"/>).
    /// </summary>
    protected static CdsMedicationIndex Medications(CdsHandlerContext context) =>
        CdsMedicationIndex.Build(
            new[] { context.Request.Context?["draftOrders"] }
                .Concat(context.Request.Prefetch?.Select(entry => entry.Value) ?? []));

    // ---- Cards: the instance's dismiss reasons, links and one-click fixes ----

    /// <summary>The handler's own links (may be null) followed by the instance's configured links; null when none.</summary>
    protected static IReadOnlyList<CdsLink>? CardLinks(CdsHandlerContext context, IReadOnlyList<CdsLink>? own = null)
    {
        var links = (own ?? []).Concat(context.Links).ToArray();
        return links.Length > 0 ? links : null;
    }

    protected static string? PatientId(CdsHandlerContext context) => CdsFhirReader.Text(context.Request.Context?["patientId"]);

    protected static string? EncounterId(CdsHandlerContext context) => CdsFhirReader.Text(context.Request.Context?["encounterId"]);

    /// <summary>"Type/id" of an order (what Epic's delete action names), or null when it has no id.</summary>
    protected static string? OrderReference(JsonObject order) =>
        CdsFhirReader.ResourceType(order) is { } type && CdsFhirReader.Text(order["id"]) is { Length: > 0 } id ? $"{type}/{id}" : null;

    /// <summary>
    /// The mapping of a switched-on fix for the first of <paramref name="triggers"/> that has its own mapping, else
    /// the fix's catch-all mapping (no trigger); null when the fix is off or nothing applies.
    /// </summary>
    protected static CdsFixCodeMapping? FindMapping(CdsHandlerContext context, string fixTypeKey, IEnumerable<string>? triggers)
    {
        foreach (var trigger in triggers ?? [])
        {
            if (context.FindFixMapping(fixTypeKey, trigger) is { } mapping && string.Equals(mapping.Trigger, trigger, StringComparison.Ordinal))
            {
                return mapping;
            }
        }

        return context.FindFixMapping(fixTypeKey);
    }

    /// <summary>
    /// The fixes for a card about a draft medication order: remove it (delete, the order's own "Type/id") and order
    /// the mapped alternative instead (create), then the diagnosis fixes. Each is null when its fix type is off.
    /// </summary>
    protected static IEnumerable<CdsSuggestion?> MedicationOrderFixes(
        CdsHandlerContext context,
        JsonObject order,
        string orderLabel,
        IEnumerable<string>? alternativeTriggers,
        IEnumerable<string>? diagnosisTriggers)
    {
        var suggestions = new List<CdsSuggestion?>();
        if (CdsFhirReader.ResourceType(order) == "MedicationRequest" && OrderReference(order) is { } reference)
        {
            suggestions.Add(context.CreateDeleteSuggestion(
                CdsFixTypeKeys.RemoveDraftOrder,
                $"Remove {orderLabel}",
                reference,
                $"Removes the unsigned order for {orderLabel}."));
        }

        if (PatientId(context) is { } patientId
            && FindMapping(context, CdsFixTypeKeys.AddAlternativeMedication, alternativeTriggers) is { } alternative)
        {
            suggestions.Add(context.CreateCreateSuggestion(
                CdsFixTypeKeys.AddAlternativeMedication,
                $"Order {MappingName(alternative)} instead",
                CdsFixResources.MedicationRequest(alternative, patientId),
                $"Adds an unsigned order for {MappingName(alternative)}."));
        }

        suggestions.AddRange(DiagnosisFixes(context, diagnosisTriggers));
        return suggestions;
    }

    /// <summary>
    /// Add to the problem list and (inside an encounter only) add an encounter diagnosis, each from its mapping for
    /// the first matching trigger; null entries for fix types that are off.
    /// </summary>
    protected static IEnumerable<CdsSuggestion?> DiagnosisFixes(CdsHandlerContext context, IEnumerable<string>? triggers)
    {
        if (PatientId(context) is not { } patientId)
        {
            return [];
        }

        var triggerList = triggers?.ToArray();
        var suggestions = new List<CdsSuggestion?>();
        if (FindMapping(context, CdsFixTypeKeys.AddProblem, triggerList) is { } problem)
        {
            suggestions.Add(context.CreateCreateSuggestion(
                CdsFixTypeKeys.AddProblem,
                $"Add {MappingName(problem)} to the problem list",
                CdsFixResources.Condition(problem, patientId, "problem-list-item"),
                $"Adds {MappingName(problem)} to the problem list."));
        }

        if (EncounterId(context) is { Length: > 0 } encounterId
            && FindMapping(context, CdsFixTypeKeys.AddEncounterDiagnosis, triggerList) is { } diagnosis)
        {
            suggestions.Add(context.CreateCreateSuggestion(
                CdsFixTypeKeys.AddEncounterDiagnosis,
                $"Add {MappingName(diagnosis)} as an encounter diagnosis",
                CdsFixResources.Condition(diagnosis, patientId, "encounter-diagnosis", encounterId),
                $"Adds {MappingName(diagnosis)} to this encounter's diagnoses."));
        }

        return suggestions;
    }

    /// <summary>A mapping's display name, else its code.</summary>
    protected static string MappingName(CdsFixCodeMapping mapping) =>
        string.IsNullOrWhiteSpace(mapping.Display) ? mapping.Code : mapping.Display;

    /// <summary>The detail markdown with the SAMPLE notice appended.</summary>
    protected static string Detail(params string[] paragraphs) =>
        string.Join("\n\n", paragraphs.Where(p => !string.IsNullOrWhiteSpace(p)).Append(CdsSampleReferenceData.CardNotice));

    protected static IReadOnlyDictionary<string, string> NoPrefetch { get; } = new Dictionary<string, string>();

    // ---- Synthetic sample requests (made-up data only, in the shape Epic sends) ----

    /// <summary>
    /// A request shaped like Epic's: userId as a PractitionerRole reference, encounterId, the Epic request
    /// extensions, and for order hooks a draftOrders Bundle (type collection) that also carries each referenced
    /// Medication as its own entry; order-select lists the newly selected orders in selections. No fhirServer or
    /// fhirAuthorization: the sample is complete, and the Test screen never calls an EHR back.
    /// </summary>
    protected static CdsRequest SampleRequest(string hook, IReadOnlyList<JsonObject> draftOrders, JsonObject? prefetch)
    {
        var context = new JsonObject
        {
            ["userId"] = SampleUserId,
            ["patientId"] = SamplePatientId,
            ["encounterId"] = SampleEncounterId,
        };

        if (hook is CdsHookNames.OrderSelect or CdsHookNames.OrderSign)
        {
            if (hook == CdsHookNames.OrderSelect)
            {
                context["selections"] = new JsonArray(draftOrders
                    .Select(order => (JsonNode?)JsonValue.Create($"{CdsFhirReader.ResourceType(order)}/{CdsFhirReader.Text(order["id"])}"))
                    .ToArray());
            }

            context["draftOrders"] = DraftOrdersBundle(draftOrders);
        }

        return new CdsRequest
        {
            Hook = hook,
            HookInstance = Guid.NewGuid().ToString(),
            Context = context,
            Prefetch = prefetch,
            Extension = SampleEpicExtension(hook),
        };
    }

    /// <summary>Made-up values for Epic's request extensions (the trigger action is a sample, not a mapping).</summary>
    private static JsonObject SampleEpicExtension(string hook) => new()
    {
        [CdsEpicExtensions.RequestBpaTriggerAction] = hook == CdsHookNames.PatientView ? "5" : "18",
        [CdsEpicExtensions.RequestSpecificationVersion] = "1.0",
        [CdsEpicExtensions.RequestFhirVersion] = "R4",
        [CdsEpicExtensions.RequestEpicVersion] = "11.2",
        [CdsEpicExtensions.RequestImplementationVersion] = "2",
    };

    /// <summary>
    /// draftOrders as Epic sends it: a collection Bundle with each order, plus (for a MedicationRequest with a
    /// medicationReference "Medication/id") the Medication as its own entry, taken from the order's
    /// <c>contained</c> sample list and removed from it.
    /// </summary>
    private static JsonObject DraftOrdersBundle(IReadOnlyList<JsonObject> draftOrders)
    {
        var entries = new List<JsonObject>();
        foreach (var order in draftOrders)
        {
            JsonObject[] medications = [];
            if (order["contained"] is JsonArray contained
                && CdsFhirReader.Text((order["medicationReference"] as JsonObject)?["reference"]) is { } reference
                && !reference.StartsWith('#'))
            {
                medications = contained.OfType<JsonObject>().ToArray();
                contained.Clear();
                order.Remove("contained");
            }

            entries.Add(new JsonObject
            {
                ["fullUrl"] = $"{SampleFhirBase}/{CdsFhirReader.ResourceType(order)}/{CdsFhirReader.Text(order["id"])}",
                ["resource"] = order,
            });
            foreach (var medication in medications)
            {
                entries.Add(new JsonObject
                {
                    ["fullUrl"] = $"{SampleFhirBase}/Medication/{CdsFhirReader.Text(medication["id"])}",
                    ["resource"] = medication,
                });
            }
        }

        return new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "collection",
            ["entry"] = new JsonArray(entries.Select(entry => (JsonNode?)entry).ToArray()),
        };
    }

    private const string SampleFhirBase = "https://ehr.example.org/api/FHIR/R4";

    protected static JsonObject Bundle(string type, IEnumerable<JsonObject> resources) => new()
    {
        ["resourceType"] = "Bundle",
        ["type"] = type,
        ["entry"] = new JsonArray(resources.Select(resource => (JsonNode?)new JsonObject { ["resource"] = resource }).ToArray()),
    };

    protected static JsonObject SearchSet(params JsonObject[] resources) => Bundle("searchset", resources);

    protected static JsonObject Concept(string system, string code, string display) => new()
    {
        ["coding"] = new JsonArray(new JsonObject { ["system"] = system, ["code"] = code, ["display"] = display }),
        ["text"] = display,
    };

    protected static JsonObject PatientReference() => new() { ["reference"] = $"Patient/{SamplePatientId}" };

    /// <summary>
    /// A MedicationRequest in Epic's current shape: a medicationReference (upper-case display) to a Medication that
    /// carries the RxNorm code. A draft order references "Medication/id" (the Medication becomes its own draftOrders
    /// entry, see <see cref="DraftOrdersBundle"/>); any other one references a contained "#id".
    /// </summary>
    protected static JsonObject SampleMedicationRequest(string id, string status, string rxNormCode, string display)
    {
        var medicationId = $"med-{id}";
        var draft = status == "draft";
        return new JsonObject
        {
            ["resourceType"] = "MedicationRequest",
            ["id"] = id,
            ["status"] = status,
            ["intent"] = draft ? "proposal" : "order",
            ["contained"] = new JsonArray(new JsonObject
            {
                ["resourceType"] = "Medication",
                ["id"] = medicationId,
                ["code"] = Concept(CdsSampleReferenceData.RxNormSystem, rxNormCode, display),
            }),
            ["medicationReference"] = new JsonObject
            {
                ["reference"] = draft ? $"Medication/{medicationId}" : $"#{medicationId}",
                ["display"] = display.ToUpperInvariant(),
            },
            ["subject"] = PatientReference(),
        };
    }

    protected static string SampleDate(int daysAgo) =>
        DateTime.UtcNow.Date.AddDays(-daysAgo).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
