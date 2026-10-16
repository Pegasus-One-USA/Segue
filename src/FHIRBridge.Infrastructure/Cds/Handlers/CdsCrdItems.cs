using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.DTOs.Cds;

namespace FHIRBridge.Infrastructure.Cds.Handlers;

/// <summary>
/// One thing a coverage requirements (CRD) call asks about: a draft / dispatched order, an appointment being booked,
/// or the encounter starting / being discharged.
/// </summary>
/// <param name="Key">"Type/id", or "#index" when the resource has no id (it then cannot be updated).</param>
/// <param name="Reference">"Type/id"; null without an id.</param>
/// <param name="Codes">The codings the payer rules are matched against (only codings with a code).</param>
/// <param name="Label">A short human label (display text, else code).</param>
internal sealed record CdsCrdItem(
    string Key,
    JsonObject Resource,
    string ResourceType,
    string? Reference,
    IReadOnlyList<FhirCode> Codes,
    string Label);

/// <summary>
/// Reads the items of a CRD request per hook (pure, never throws): order-sign → context.draftOrders; order-select →
/// the selected draft orders (context.selections), else all; order-dispatch → the resources named in
/// context.dispatchedOrders, found in prefetch or in context.fulfillmentTasks; appointment-book →
/// context.appointments; encounter-start / encounter-discharge → the Encounter context.encounterId names, from prefetch.
/// Used by both the coverage engine (to match rules) and the handler (to build the answer), so both see the same keys.
/// </summary>
internal static class CdsCrdItems
{
    public static IReadOnlyList<CdsCrdItem> Extract(CdsRequest request)
    {
        var context = request.Context;
        IReadOnlyList<JsonObject> resources = request.Hook switch
        {
            CdsHookNames.OrderSign => DraftOrders(context),
            CdsHookNames.OrderSelect => Selected(context),
            CdsHookNames.OrderDispatch => Dispatched(request),
            CdsHookNames.AppointmentBook => CdsFhirReader.Resources(context?["appointments"], "Appointment"),
            CdsHookNames.EncounterStart or CdsHookNames.EncounterDischarge => Encounter(request),
            _ => [],
        };

        var medications = CdsMedicationIndex.Build(
            new[] { context?["draftOrders"] }.Concat(request.Prefetch?.Select(entry => entry.Value) ?? []));
        var items = new List<CdsCrdItem>();
        for (var i = 0; i < resources.Count; i++)
        {
            var resource = resources[i];
            var type = CdsFhirReader.ResourceType(resource) ?? "Resource";
            var id = CdsFhirReader.Text(resource["id"]);
            var reference = string.IsNullOrEmpty(id) ? null : $"{type}/{id}";
            var concepts = Concepts(resource, type, medications);
            var codes = concepts.SelectMany(CdsFhirReader.Codings).Where(code => code.Code is not null).ToArray();
            var label = concepts.Select(concept => CdsFhirReader.Label(concept, string.Empty)).FirstOrDefault(text => text.Length > 0)
                        ?? FallbackLabel(type);
            items.Add(new CdsCrdItem(reference ?? $"#{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}", resource, type, reference, codes, label));
        }

        return items;
    }

    /// <summary>Every "Type/id" a systemAction of this request may update (its own orders, appointments, encounter).</summary>
    public static IReadOnlySet<string> References(CdsRequest request) =>
        Extract(request).Select(item => item.Reference).OfType<string>().ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyList<JsonObject> DraftOrders(JsonObject? context) =>
        CdsFhirReader.Resources(context?["draftOrders"])
            .Where(resource => CdsFhirReader.IsOrderType(CdsFhirReader.ResourceType(resource)))
            .ToArray();

    private static IReadOnlyList<JsonObject> Selected(JsonObject? context)
    {
        var drafts = DraftOrders(context);
        if (context?["selections"] is not JsonArray selections)
        {
            return drafts;
        }

        var selected = selections.Select(CdsFhirReader.Text).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var matching = drafts.Where(order => selected.Contains($"{CdsFhirReader.ResourceType(order)}/{CdsFhirReader.Text(order["id"])}")).ToArray();
        return matching.Length > 0 ? matching : drafts;
    }

    private static IReadOnlyList<JsonObject> Dispatched(CdsRequest request)
    {
        var wanted = (request.Context?["dispatchedOrders"] as JsonArray ?? [])
            .Select(CdsFhirReader.Text)
            .OfType<string>()
            .Select(reference => reference.Trim())
            .ToArray();
        if (wanted.Length == 0)
        {
            return [];
        }

        var pool = new List<JsonObject>();
        foreach (var value in request.Prefetch?.Select(entry => entry.Value) ?? [])
        {
            pool.AddRange(CdsFhirReader.Resources(value));
        }

        var tasks = request.Context?["fulfillmentTasks"];
        var taskResources = tasks is JsonArray taskArray
            ? taskArray.OfType<JsonObject>().SelectMany(task => CdsFhirReader.Resources(task))
            : CdsFhirReader.Resources(tasks);
        foreach (var task in taskResources)
        {
            if (task["contained"] is JsonArray contained)
            {
                pool.AddRange(contained.OfType<JsonObject>());
            }
        }

        var found = new List<JsonObject>();
        foreach (var reference in wanted.Distinct(StringComparer.Ordinal))
        {
            var match = pool.FirstOrDefault(resource =>
                reference.EndsWith($"{CdsFhirReader.ResourceType(resource)}/{CdsFhirReader.Text(resource["id"])}", StringComparison.Ordinal));
            if (match is not null && CdsFhirReader.IsOrderType(CdsFhirReader.ResourceType(match)))
            {
                found.Add(match);
            }
        }

        return found;
    }

    private static IReadOnlyList<JsonObject> Encounter(CdsRequest request)
    {
        var encounterId = CdsFhirReader.Text(request.Context?["encounterId"]);
        var encounters = (request.Prefetch?.Select(entry => entry.Value) ?? [])
            .SelectMany(value => CdsFhirReader.Resources(value, "Encounter"))
            .ToArray();
        var match = encounters.FirstOrDefault(e => encounterId is not null && CdsFhirReader.Text(e["id"]) == encounterId)
                    ?? (encounters.Length == 1 ? encounters[0] : null);
        return match is null ? [] : [match];
    }

    /// <summary>The concepts a payer codes the item by.</summary>
    private static IReadOnlyList<JsonNode> Concepts(JsonObject resource, string type, CdsMedicationIndex medications)
    {
        var concepts = new List<JsonNode?>();
        if (type == "Appointment")
        {
            concepts.AddRange(Array(resource["serviceType"]));
            concepts.Add(resource["appointmentType"]);
            concepts.AddRange(Array(resource["serviceCategory"]));
        }
        else if (type == "Encounter")
        {
            concepts.AddRange(Array(resource["type"]));
            concepts.Add(resource["serviceType"]);
        }
        else
        {
            concepts.Add(CdsFhirReader.OrderConcept(resource, medications));
        }

        return concepts.OfType<JsonNode>().ToArray();
    }

    private static IEnumerable<JsonNode?> Array(JsonNode? node) =>
        node is JsonArray array ? array : node is null ? [] : [node];

    private static string FallbackLabel(string type) => type switch
    {
        "Appointment" => "the appointment",
        "Encounter" => "the encounter",
        _ => "the order",
    };
}
