using System.Text.Json;
using FHIRBridge.Application.Services.Tabular;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Application.Services.Workflows;

/// <summary>
/// Reads the FHIR resource types a workflow node declares in its configuration JSON, in either the flat or the
/// enveloped shape (see <see cref="WorkflowNodeConfigurationEnvelope"/>). The source node is the record of what a
/// workflow reads: its destinations choose from that list and can never widen it. Shared by the save-time
/// <see cref="WorkflowResourceTypeSubsetRule"/> and the source connection scope sync, so both read the same keys.
/// </summary>
public static class WorkflowNodeResourceTypes
{
    /// <summary>An EHR / Generic FHIR source node's "Resource types to read" (comma list).</summary>
    public const string SourceResourcesKey = "Resources";

    /// <summary>
    /// Marks an EHR / Generic FHIR source node whose <see cref="SourceResourcesKey"/> is the admin's own choice
    /// ("true"). Written by the portal's source pickers whenever they save a declared list (the portal's
    /// SOURCE_TYPES_DECLARED_KEY). A node without it was saved before sources declared their types, and its
    /// "Resources" may hold a silent default the form filled in.
    /// </summary>
    public const string SourceTypesDeclaredKey = "Resource types declared";

    /// <summary>The destination wizard's selected resource types (comma list).</summary>
    public const string DestinationResourcesKey = "dest_resources";

    /// <summary>The destination wizard's flat mapping rows (JSON array of <c>{ resource, ... }</c>).</summary>
    public const string DestinationMappingsKey = "dest_mappings";

    /// <summary>
    /// The resource types a source node declares it reads, or null when it declares none — a node saved before
    /// sources declared their types, which keeps the run-time fallbacks (connection retrieval list, scopes,
    /// destinations' selections). A CSV / SQL Table source declares them per entry in <c>tab_streams</c>, else in its
    /// older single-query <c>tab_templates</c>, else in <c>Resources</c>; every other source in <c>Resources</c> only,
    /// and only when the node carries <see cref="SourceTypesDeclaredKey"/> = "true" (any letter case, or a JSON
    /// boolean true).
    /// <para>
    /// The marker is what makes an EHR / Generic FHIR list a declaration. Before sources declared their types, the
    /// Generic FHIR form saved its 12 default types into "Resources" (and some EHR nodes a cloned connection's list)
    /// without the admin choosing them; reading those as declared would reject re-saving an unchanged legacy
    /// workflow whose destination writes another type (an Organization, say) and widen the connection's scopes to
    /// the silent default. Mirrors the portal's declaredSourceResourceTypes (upstream-source-v2.util.ts), which also
    /// treats a node as CSV / SQL Table by its <c>tab_kind</c> setting.
    /// </para>
    /// <para>
    /// The retrieval method's own "Retrieval resource type" field is deliberately NOT read as a declared list. Before
    /// sources declared their types, the EHR form pre-filled that hidden field with every supported type (Epic and
    /// others), or with the cloned connection's list (any vendor, athenahealth and eClinicalWorks included), and the
    /// types actually fetched and scoped came from the destinations. Reading it here would widen a legacy
    /// connection's scopes to that silent default (athenahealth and eClinicalWorks reject the whole token request
    /// for one unregistered scope) and reject re-saving an unchanged legacy workflow.
    /// </para>
    /// <para>
    /// Only the save-time check and the scope sync read this. The run time (SourceNodeExecutors) still fetches a
    /// node's "Resources" whether or not it is marked, narrowed to what its destinations select, exactly as before.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string>? ReadSourceDeclared(string nodeType, string? configurationJson)
    {
        using var document = TryParse(configurationJson);
        if (!WorkflowNodeConfigurationEnvelope.TryGetSettings(document, out var settings))
        {
            return null;
        }

        if (IsTabularSource(nodeType, settings))
        {
            var tabular = ReadTabularResourceTypes(settings, TabularSourceSettings.StreamsKey);
            if (tabular.Count == 0)
            {
                tabular = ReadTabularResourceTypes(settings, TabularSourceSettings.TemplatesKey);
            }

            if (tabular.Count > 0)
            {
                return tabular;
            }
        }
        else if (!HasDeclaredTypesMarker(settings))
        {
            return null;
        }

        var declared = SplitCommaList(ReadString(settings, SourceResourcesKey));
        return declared.Count > 0 ? declared : null;
    }

    // The portal saves a CSV / SQL Table node as TabularSourceNode and tells one apart by its tab_kind setting; either
    // marks it here, so a node the portal treats as tabular is never read as a legacy EHR node (or the reverse).
    private static bool IsTabularSource(string nodeType, JsonElement settings) =>
        string.Equals(nodeType, WorkflowNodeTypes.TabularSource, StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(ReadString(settings, TabularSourceSettings.KindKey));

    // "true" in any letter case, or a JSON boolean true (a hand-written or API-built node). The portal's
    // hasDeclaredTypesMarker applies the same rule; its graph mapper turns a stored boolean into the string "true".
    private static bool HasDeclaredTypesMarker(JsonElement settings) =>
        settings.ValueKind == JsonValueKind.Object
        && settings.TryGetProperty(SourceTypesDeclaredKey, out var marker)
        && (marker.ValueKind == JsonValueKind.True
            || (marker.ValueKind == JsonValueKind.String
                && string.Equals(marker.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

    /// <summary>A destination node's selected resource types (<c>dest_resources</c>); empty when it has none.</summary>
    public static IReadOnlyList<string> ReadDestinationSelected(string? configurationJson)
    {
        using var document = TryParse(configurationJson);
        return WorkflowNodeConfigurationEnvelope.TryGetSettings(document, out var settings)
            ? SplitCommaList(ReadString(settings, DestinationResourcesKey))
            : [];
    }

    /// <summary>
    /// Every resource type a destination node writes: its selected types plus the resource of each mapping row
    /// (<c>dest_mappings</c>), de-duplicated in first-seen order. Empty for a node with neither (e.g. analytics).
    /// </summary>
    public static IReadOnlyList<string> ReadDestinationWritten(string? configurationJson)
    {
        using var document = TryParse(configurationJson);
        if (!WorkflowNodeConfigurationEnvelope.TryGetSettings(document, out var settings))
        {
            return [];
        }

        var written = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in SplitCommaList(ReadString(settings, DestinationResourcesKey)))
        {
            if (seen.Add(type))
            {
                written.Add(type);
            }
        }

        foreach (var entry in ReadJsonArray(settings, DestinationMappingsKey))
        {
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("resource", out var resource)
                && resource.ValueKind == JsonValueKind.String
                && resource.GetString() is { } type
                && LooksLikeResourceType(type)
                && seen.Add(type))
            {
                written.Add(type);
            }
        }

        return written;
    }

    /// <summary>A string setting of the node, or null when absent, not a string, or the JSON is unreadable.</summary>
    public static string? ReadString(string? configurationJson, string key)
    {
        using var document = TryParse(configurationJson);
        return WorkflowNodeConfigurationEnvelope.TryGetSettings(document, out var settings)
            ? ReadString(settings, key)
            : null;
    }

    private static IReadOnlyList<string> ReadTabularResourceTypes(JsonElement settings, string key)
    {
        var types = new List<string>();
        foreach (var entry in ReadJsonArray(settings, key))
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // tab_streams names the type on the entry; a legacy tab_templates entry names it on its template.
            var type = entry.TryGetProperty("resourceType", out var direct) && direct.ValueKind == JsonValueKind.String
                ? direct.GetString()
                : entry.TryGetProperty("template", out var template)
                  && template.ValueKind == JsonValueKind.Object
                  && template.TryGetProperty("resourceType", out var nested)
                  && nested.ValueKind == JsonValueKind.String
                    ? nested.GetString()
                    : null;

            if (!string.IsNullOrWhiteSpace(type) && !types.Contains(type.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                types.Add(type.Trim());
            }
        }

        return types;
    }

    // The wizard's field bag is Record<string,string>, so a JSON-valued setting arrives as a JSON string; a
    // hand-authored node may carry the array itself. Both are read; anything unreadable is treated as absent.
    private static IReadOnlyList<JsonElement> ReadJsonArray(JsonElement settings, string key)
    {
        if (!settings.TryGetProperty(key, out var value))
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            return [.. value.EnumerateArray().Select(element => element.Clone())];
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            return [];
        }

        try
        {
            using var inner = JsonDocument.Parse(value.GetString()!);
            return inner.RootElement.ValueKind == JsonValueKind.Array
                ? [.. inner.RootElement.EnumerateArray().Select(element => element.Clone())]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ReadString(JsonElement settings, string key) =>
        settings.ValueKind == JsonValueKind.Object
        && settings.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> SplitCommaList(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    // A mapping row's "resource" is the FHIR type it reads from; anything else there (blank, a display label) is
    // not a type the source could be asked to read, so it is not checked.
    private static bool LooksLikeResourceType(string value) =>
        value.Length > 0 && char.IsAsciiLetterUpper(value[0]) && value.All(char.IsAsciiLetter);

    private static JsonDocument? TryParse(string? configurationJson)
    {
        if (string.IsNullOrWhiteSpace(configurationJson))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(configurationJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
