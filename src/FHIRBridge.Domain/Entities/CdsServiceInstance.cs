using System.Text.Json;
using System.Text.RegularExpressions;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Abstractions;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// One CDS Hooks service Segue publishes at <c>/cds-services/{ServiceId}</c>: a configured instance of a built-in
/// handler type (<see cref="HandlerKey"/>, e.g. "drug-allergy") bound to one <see cref="Hook"/>, with its own
/// <see cref="SettingsJson"/>. Admins can create any number of instances of the same handler type.
/// </summary>
public sealed partial class CdsServiceInstance : AuditableEntity<Guid>, IHasAuditDisplayName
{
    public const int ServiceIdMinLength = 3;
    public const int ServiceIdMaxLength = 100;
    public const int TitleMaxLength = 200;
    public const int HandlerKeyMaxLength = 100;
    public const int HookMaxLength = 100;
    public const int DescriptionMaxLength = 2000;
    public const int CardSourceLabelMaxLength = 200;
    public const int UrlMaxLength = 1000;

    private CdsServiceInstance()
    {
    }

    public CdsServiceInstance(
        string serviceId,
        string title,
        string handlerKey,
        string hook,
        string description,
        bool isEnabled,
        string? settingsJson,
        string cardSourceLabel,
        string? cardSourceUrl,
        IEnumerable<Guid>? allowedIssuerIds,
        bool fetchMissingData = true,
        CdsFixSettings? fixes = null,
        IEnumerable<CdsOverrideReasonOption>? overrideReasons = null,
        IEnumerable<CdsCardLinkOption>? links = null)
    {
        Id = Guid.NewGuid();
        Update(serviceId, title, handlerKey, hook, description, isEnabled, settingsJson, cardSourceLabel, cardSourceUrl, allowedIssuerIds,
            fetchMissingData, fixes, overrideReasons, links);
    }

    /// <summary>The URL slug: lower-case letters, digits and single hyphens (e.g. "drug-allergy-check").</summary>
    public string ServiceId { get; private set; } = default!;

    public string Title { get; private set; } = default!;
    string? IHasAuditDisplayName.AuditDisplayName => Title;

    public string HandlerKey { get; private set; } = default!;

    /// <summary>The CDS Hooks hook this instance answers (e.g. "order-sign", "patient-view").</summary>
    public string Hook { get; private set; } = default!;

    public string Description { get; private set; } = default!;

    public bool IsEnabled { get; private set; }

    /// <summary>The handler's settings as a JSON object; keys follow the handler's settings descriptor.</summary>
    public string SettingsJson { get; private set; } = "{}";

    /// <summary>The <c>source.label</c> every card from this instance carries.</summary>
    public string CardSourceLabel { get; private set; } = default!;

    public string? CardSourceUrl { get; private set; }

    /// <summary>
    /// Trusted issuers allowed to call this instance. Empty means any enabled trusted issuer.
    /// </summary>
    public List<Guid> AllowedIssuerIds { get; private set; } = [];

    /// <summary>
    /// When a prefetch key is missing, fetch it from the request's <c>fhirServer</c> with its
    /// <c>fhirAuthorization</c> token (the trusted issuer must also allow callbacks). On by default.
    /// </summary>
    public bool FetchMissingData { get; private set; } = true;

    /// <summary>
    /// The one-click fixes (<see cref="CdsFixSettings"/>) as JSON, or null when never configured (fixes off).
    /// Read through <see cref="GetFixes"/>.
    /// </summary>
    public string? FixesJson { get; private set; }

    /// <summary>The dismiss reasons (<see cref="CdsOverrideReasonOption"/> list) as JSON, or null for the defaults.</summary>
    public string? OverrideReasonsJson { get; private set; }

    /// <summary>The card links (<see cref="CdsCardLinkOption"/> list) as JSON, or null for none.</summary>
    public string? LinksJson { get; private set; }

    /// <summary>The fix settings; <see cref="CdsFixSettings.Disabled"/> when none are stored.</summary>
    public CdsFixSettings GetFixes() =>
        CdsServiceOptionsRules.Deserialize<CdsFixSettings>(FixesJson) ?? CdsFixSettings.Disabled;

    /// <summary>
    /// True when cards may carry fixes (they change EHR draft orders once accepted): the master switch is on and at
    /// least one fix type is on. The master switch alone, with every type off, offers nothing.
    /// </summary>
    public bool FixesEnabled => GetFixes() is { Enabled: true } fixes && fixes.FixTypes.Any(fixType => fixType.Enabled);

    /// <summary>The instance's own dismiss reasons, or null when it uses <see cref="CdsOverrideReasonOption.Defaults"/>.</summary>
    public IReadOnlyList<CdsOverrideReasonOption>? GetOwnOverrideReasons() =>
        CdsServiceOptionsRules.Deserialize<List<CdsOverrideReasonOption>>(OverrideReasonsJson) is { Count: > 0 } list ? list : null;

    /// <summary>The dismiss reasons cards offer: the instance's own list, else the defaults.</summary>
    public IReadOnlyList<CdsOverrideReasonOption> GetOverrideReasons() =>
        GetOwnOverrideReasons() ?? CdsOverrideReasonOption.Defaults;

    public IReadOnlyList<CdsCardLinkOption> GetLinks() =>
        CdsServiceOptionsRules.Deserialize<List<CdsCardLinkOption>>(LinksJson) ?? [];

    public void Update(
        string serviceId,
        string title,
        string handlerKey,
        string hook,
        string description,
        bool isEnabled,
        string? settingsJson,
        string cardSourceLabel,
        string? cardSourceUrl,
        IEnumerable<Guid>? allowedIssuerIds,
        bool fetchMissingData = true,
        CdsFixSettings? fixes = null,
        IEnumerable<CdsOverrideReasonOption>? overrideReasons = null,
        IEnumerable<CdsCardLinkOption>? links = null)
    {
        ServiceId = NormalizeServiceId(serviceId);
        Title = Required(title, TitleMaxLength, "Title");
        HandlerKey = Required(handlerKey, HandlerKeyMaxLength, "Handler");
        Hook = Required(hook, HookMaxLength, "Hook");
        Description = Required(description, DescriptionMaxLength, "Description");
        IsEnabled = isEnabled;
        SettingsJson = NormalizeSettings(settingsJson);
        CardSourceLabel = Required(cardSourceLabel, CardSourceLabelMaxLength, "Card source label");
        CardSourceUrl = OptionalAbsoluteUrl(cardSourceUrl, "Card source URL");
        AllowedIssuerIds = (allowedIssuerIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
        FetchMissingData = fetchMissingData;
        FixesJson = CdsServiceOptionsRules.Serialize(CdsServiceOptionsRules.NormalizeFixes(fixes));
        OverrideReasonsJson = CdsServiceOptionsRules.Serialize(CdsServiceOptionsRules.NormalizeOverrideReasons(overrideReasons));
        LinksJson = CdsServiceOptionsRules.Serialize(CdsServiceOptionsRules.NormalizeLinks(links));
    }

    public void SetEnabled(bool isEnabled) => IsEnabled = isEnabled;

    /// <summary>True when the trusted issuer may call this instance (see <see cref="AllowedIssuerIds"/>).</summary>
    public bool AllowsIssuer(Guid trustedIssuerId) =>
        AllowedIssuerIds.Count == 0 || AllowedIssuerIds.Contains(trustedIssuerId);

    /// <summary>Trims and lower-cases, then enforces the slug rules. Exposed so a lookup can normalize the same way.</summary>
    public static string NormalizeServiceId(string? serviceId)
    {
        var normalized = serviceId?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length < ServiceIdMinLength || normalized.Length > ServiceIdMaxLength)
        {
            throw new BusinessRuleException(
                $"Service id must be {ServiceIdMinLength} to {ServiceIdMaxLength} characters.");
        }

        if (!ServiceIdPattern().IsMatch(normalized))
        {
            throw new BusinessRuleException(
                "Service id may contain only lower-case letters, digits and single hyphens, and must start and end with a letter or digit.");
        }

        return normalized;
    }

    public static bool IsValidServiceId(string? serviceId) =>
        serviceId is { Length: >= ServiceIdMinLength and <= ServiceIdMaxLength } && ServiceIdPattern().IsMatch(serviceId);

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceIdPattern();

    private static string NormalizeSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new BusinessRuleException("Settings must be a JSON object.");
            }

            return document.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            throw new BusinessRuleException("Settings are not valid JSON.");
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

    private static string? OptionalAbsoluteUrl(string? value, string label)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > UrlMaxLength)
        {
            throw new BusinessRuleException($"{label} must be {UrlMaxLength} characters or fewer.");
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new BusinessRuleException($"{label} must be an absolute http(s) URL.");
        }

        return trimmed;
    }
}
