using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Domain.ValueObjects;

// Per-instance CDS Hooks options stored as JSON on CdsServiceInstance (FixesJson, OverrideReasonsJson, LinksJson).
// Shape rules live here; rules that depend on the handler type (which fix types it supports, which mapping
// fields a fix type needs) are checked by the Application admin service against the handler's descriptor.

/// <summary>
/// One reason a clinician can pick when dismissing a card (CDS Hooks <c>overrideReasons</c>). Epic: the health
/// system maps each <see cref="Code"/> to one of its own override reasons, so codes must stay stable.
/// </summary>
/// <param name="Code">1-50 characters: letters, digits, '-' and '_'.</param>
/// <param name="Display">1-200 characters, shown to the clinician.</param>
public sealed record CdsOverrideReasonOption(string Code, string Display)
{
    public const int MinCount = 1;
    public const int MaxCount = 10;
    public const int CodeMaxLength = 50;
    public const int DisplayMaxLength = 200;

    /// <summary>The coding system Segue puts on every override reason it offers.</summary>
    public const string System = "urn:segue:cds:override-reason";

    /// <summary>Used when an instance has no list of its own (the v1 standard reasons).</summary>
    public static IReadOnlyList<CdsOverrideReasonOption> Defaults { get; } =
    [
        new("patient-tolerated", "Patient has tolerated this before"),
        new("benefit-outweighs-risk", "Benefit outweighs the risk"),
        new("will-monitor", "Will monitor the patient"),
        new("not-applicable", "Alert does not apply to this patient"),
    ];
}

/// <summary>The allowed values of <see cref="CdsCardLinkOption.Type"/>.</summary>
public static class CdsCardLinkTypes
{
    public const string Absolute = "absolute";
    public const string Smart = "smart";

    public static readonly IReadOnlyList<string> All = [Absolute, Smart];
}

/// <summary>
/// A link button added to every card of an instance (CDS Hooks <c>links</c>). For Epic, a <c>smart</c> link must be
/// on the health system's SMART app allow-list, so the setup sheet lists every link.
/// </summary>
/// <param name="Label">1-100 characters.</param>
/// <param name="Url">Absolute https URL, up to 1000 characters.</param>
/// <param name="Type"><see cref="CdsCardLinkTypes.Absolute"/> or <see cref="CdsCardLinkTypes.Smart"/>.</param>
/// <param name="AppContext">smart links only: an opaque string passed to the app at launch (up to 1000 characters).</param>
/// <param name="Autolaunchable">smart links only: Epic (Aug 2023+) may launch the app without a click.</param>
public sealed record CdsCardLinkOption(
    string Label,
    string Url,
    string Type,
    string? AppContext = null,
    bool Autolaunchable = false)
{
    public const int MaxCount = 10;
    public const int LabelMaxLength = 100;
    public const int UrlMaxLength = 1000;
    public const int AppContextMaxLength = 1000;
}

/// <summary>
/// One-click fixes (CDS Hooks suggestions) for an instance. Off unless <see cref="Enabled"/> is true: a fix
/// changes the clinician's draft orders or problem list in the EHR once accepted.
/// </summary>
/// <param name="Enabled">Master switch. False means cards carry no suggestions whatever the per-type switches say.</param>
/// <param name="FixTypes">Per fix type (handler descriptor key): its switch and code mappings.</param>
/// <param name="RecommendedFixType">The fix type whose suggestion is sent with <c>isRecommended: true</c>, or null.</param>
public sealed record CdsFixSettings(
    bool Enabled,
    IReadOnlyList<CdsFixTypeSetting> FixTypes,
    string? RecommendedFixType = null)
{
    public static CdsFixSettings Disabled { get; } = new(false, []);

    /// <summary>The settings of <paramref name="fixTypeKey"/> when it is switched on and the master switch is on.</summary>
    public CdsFixTypeSetting? EnabledFixType(string fixTypeKey) =>
        Enabled
            ? FixTypes.FirstOrDefault(x => x.Enabled && string.Equals(x.Key, fixTypeKey, StringComparison.Ordinal))
            : null;
}

/// <param name="Key">A fix type key from the handler's descriptor (e.g. "remove-draft-order").</param>
/// <param name="Mappings">
/// What the fix creates (e.g. the alternative drug's preference-list item or RxNorm code). Empty for a fix that
/// needs no code (removing a draft order).
/// </param>
public sealed record CdsFixTypeSetting(
    string Key,
    bool Enabled,
    IReadOnlyList<CdsFixCodeMapping> Mappings)
{
    public const int KeyMaxLength = 100;
    public const int MaxMappings = 50;
}

/// <summary>
/// One code a fix uses when it creates a resource in the EHR.
/// </summary>
/// <param name="Trigger">
/// What the mapping applies to, from the fix type's trigger options (e.g. a drug class "penicillins" or a care gap
/// "hba1c"). Null means "any trigger" (used when no more specific mapping matches).
/// </param>
/// <param name="System">
/// The code system, e.g. <c>urn:com.epic.cdshooks.action.code.system.preference-list-item</c>,
/// <c>urn:oid:2.16.840.1.113883.6.88</c> (RxNorm) or <c>urn:oid:2.16.840.1.113883.6.90</c> (ICD-10-CM).
/// </param>
public sealed record CdsFixCodeMapping(
    string? Trigger,
    string System,
    string Code,
    string? Display = null)
{
    public const int TriggerMaxLength = 100;
    public const int SystemMaxLength = 200;
    public const int CodeMaxLength = 100;
    public const int DisplayMaxLength = 200;
}

/// <summary>Shape validation and (de)serialization of the per-instance CDS options.</summary>
public static partial class CdsServiceOptionsRules
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex OverrideReasonCodePattern();

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex FixTypeKeyPattern();

    /// <summary>True for an override reason code Segue accepts: 1-50 letters, digits, '-' or '_'.</summary>
    public static bool IsValidOverrideReasonCode(string? code) =>
        code is { Length: > 0 and <= CdsOverrideReasonOption.CodeMaxLength } && OverrideReasonCodePattern().IsMatch(code);

    /// <summary>Null or empty means "use the defaults" (stored as null). Otherwise 1-10 unique, trimmed reasons.</summary>
    public static IReadOnlyList<CdsOverrideReasonOption>? NormalizeOverrideReasons(IEnumerable<CdsOverrideReasonOption?>? reasons)
    {
        var list = (reasons ?? []).ToList();
        if (list.Count == 0)
        {
            return null;
        }

        if (list.Count > CdsOverrideReasonOption.MaxCount)
        {
            throw new BusinessRuleException($"Enter at most {CdsOverrideReasonOption.MaxCount} dismiss reasons.");
        }

        var normalized = new List<CdsOverrideReasonOption>();
        foreach (var reason in list)
        {
            var code = reason?.Code?.Trim();
            if (!IsValidOverrideReasonCode(code))
            {
                throw new BusinessRuleException(
                    $"Each dismiss reason code must be 1 to {CdsOverrideReasonOption.CodeMaxLength} characters: letters, digits, '-' or '_'.");
            }

            var display = Required(reason!.Display, CdsOverrideReasonOption.DisplayMaxLength, $"The text of dismiss reason '{code}'");
            if (normalized.Any(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase)))
            {
                throw new BusinessRuleException($"The dismiss reason code '{code}' is used twice.");
            }

            normalized.Add(new CdsOverrideReasonOption(code!, display));
        }

        return normalized;
    }

    /// <summary>At most 10 links: https URLs; appContext and autolaunchable only on smart links.</summary>
    public static IReadOnlyList<CdsCardLinkOption>? NormalizeLinks(IEnumerable<CdsCardLinkOption?>? links)
    {
        var list = (links ?? []).ToList();
        if (list.Count == 0)
        {
            return null;
        }

        if (list.Count > CdsCardLinkOption.MaxCount)
        {
            throw new BusinessRuleException($"Enter at most {CdsCardLinkOption.MaxCount} links.");
        }

        var normalized = new List<CdsCardLinkOption>();
        foreach (var link in list)
        {
            if (link is null)
            {
                throw new BusinessRuleException("A link is empty.");
            }

            var label = Required(link.Label, CdsCardLinkOption.LabelMaxLength, "Link label");
            var url = Required(link.Url, CdsCardLinkOption.UrlMaxLength, $"The URL of link '{label}'");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new BusinessRuleException($"The URL of link '{label}' must be an absolute https URL.");
            }

            var type = link.Type?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!CdsCardLinkTypes.All.Contains(type))
            {
                throw new BusinessRuleException($"The type of link '{label}' must be 'absolute' or 'smart'.");
            }

            var appContext = string.IsNullOrWhiteSpace(link.AppContext) ? null : link.AppContext.Trim();
            if (type != CdsCardLinkTypes.Smart && (appContext is not null || link.Autolaunchable))
            {
                throw new BusinessRuleException($"Link '{label}': app context and auto-launch apply to SMART app links only.");
            }

            if (appContext is { Length: > CdsCardLinkOption.AppContextMaxLength })
            {
                throw new BusinessRuleException(
                    $"The app context of link '{label}' must be {CdsCardLinkOption.AppContextMaxLength} characters or fewer.");
            }

            normalized.Add(new CdsCardLinkOption(label, url, type, appContext, type == CdsCardLinkTypes.Smart && link.Autolaunchable));
        }

        return normalized;
    }

    /// <summary>
    /// Null means fixes off (stored as null). Checks shape only: unique fix type keys, mapping lengths, and that
    /// a recommended fix type is one of the switched-on types.
    /// </summary>
    public static CdsFixSettings? NormalizeFixes(CdsFixSettings? fixes)
    {
        if (fixes is null)
        {
            return null;
        }

        var types = new List<CdsFixTypeSetting>();
        foreach (var type in fixes.FixTypes ?? [])
        {
            if (type is null)
            {
                throw new BusinessRuleException("A fix type entry is empty.");
            }

            var key = type.Key?.Trim() ?? string.Empty;
            if (key.Length is 0 or > CdsFixTypeSetting.KeyMaxLength || !FixTypeKeyPattern().IsMatch(key))
            {
                throw new BusinessRuleException("A fix type key is not valid.");
            }

            if (types.Any(x => x.Key == key))
            {
                throw new BusinessRuleException($"The fix type '{key}' is listed twice.");
            }

            var mappings = (type.Mappings ?? []).ToList();
            if (mappings.Count > CdsFixTypeSetting.MaxMappings)
            {
                throw new BusinessRuleException($"Fix '{key}' has more than {CdsFixTypeSetting.MaxMappings} code mappings.");
            }

            var normalizedMappings = new List<CdsFixCodeMapping>();
            foreach (var mapping in mappings)
            {
                if (mapping is null)
                {
                    throw new BusinessRuleException($"Fix '{key}' has an empty code mapping.");
                }

                var trigger = Optional(mapping.Trigger, CdsFixCodeMapping.TriggerMaxLength, $"Fix '{key}': trigger");
                if (normalizedMappings.Any(x => string.Equals(x.Trigger, trigger, StringComparison.Ordinal)))
                {
                    throw new BusinessRuleException(
                        trigger is null
                            ? $"Fix '{key}' has two mappings for any trigger."
                            : $"Fix '{key}' has two mappings for '{trigger}'.");
                }

                normalizedMappings.Add(new CdsFixCodeMapping(
                    trigger,
                    Required(mapping.System, CdsFixCodeMapping.SystemMaxLength, $"Fix '{key}': code system"),
                    Required(mapping.Code, CdsFixCodeMapping.CodeMaxLength, $"Fix '{key}': code"),
                    Optional(mapping.Display, CdsFixCodeMapping.DisplayMaxLength, $"Fix '{key}': display")));
            }

            types.Add(new CdsFixTypeSetting(key, type.Enabled, normalizedMappings));
        }

        var recommended = string.IsNullOrWhiteSpace(fixes.RecommendedFixType) ? null : fixes.RecommendedFixType.Trim();
        if (recommended is not null && !types.Any(x => x.Key == recommended && x.Enabled))
        {
            throw new BusinessRuleException("The recommended fix must be one of the fixes that are switched on.");
        }

        return new CdsFixSettings(fixes.Enabled, types, recommended);
    }

    public static string? Serialize<T>(T? value)
        where T : class =>
        value is null ? null : JsonSerializer.Serialize(value, JsonOptions);

    /// <summary>Lenient: a value that cannot be read gives null (the caller then uses its default).</summary>
    public static T? Deserialize<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Required(string? value, int maxLength, string label)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new BusinessRuleException($"{label} is required.");
        }

        if (trimmed.Length > maxLength)
        {
            throw new BusinessRuleException($"{label} must be {maxLength} characters or fewer.");
        }

        return trimmed;
    }

    private static string? Optional(string? value, int maxLength, string label)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > maxLength)
        {
            throw new BusinessRuleException($"{label} must be {maxLength} characters or fewer.");
        }

        return trimmed;
    }
}
