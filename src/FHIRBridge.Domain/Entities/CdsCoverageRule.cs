using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Abstractions;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// One local coverage rule of a <see cref="CdsPayer"/>: for orders coded <see cref="System"/> | <see cref="Code"/>
/// (or any code starting with <see cref="Code"/> when <see cref="CodePrefixMatch"/>), what the payer requires —
/// the Da Vinci CRD coverage-information values (covered, pa-needed, doc-needed, doc-purpose, info-needed) plus a
/// plain reason, a policy link and an optional DTR questionnaire. Effective from / to (inclusive dates).
/// Unique per (payer, system, code, effective from) among live rows. Payer configuration only: no patient data.
/// </summary>
public sealed class CdsCoverageRule : AuditableEntity<Guid>, IHasAuditDisplayName
{
    public const int SystemMaxLength = 200;
    public const int CodeMaxLength = 100;
    public const int ReasonMaxLength = 500;
    public const int UrlMaxLength = 1000;

    /// <summary>The effective-from date used when none is given ("always").</summary>
    public static readonly DateOnly DefaultEffectiveFrom = new(2000, 1, 1);

    private CdsCoverageRule()
    {
    }

    public CdsCoverageRule(Guid payerId, CdsCoverageRuleValues values)
    {
        Id = Guid.NewGuid();
        PayerId = payerId;
        Update(values);
    }

    public Guid PayerId { get; private set; }

    /// <summary>The order code's system, e.g. http://www.ama-assn.org/go/cpt. Compared case-insensitively.</summary>
    public string System { get; private set; } = default!;

    public string Code { get; private set; } = default!;
    string? IHasAuditDisplayName.AuditDisplayName => $"{System}|{Code}";

    /// <summary>When true, the rule applies to every code that starts with <see cref="Code"/> (e.g. "705" for 70540-70559).</summary>
    public bool CodePrefixMatch { get; private set; }

    /// <summary><see cref="CdsCoverageCodes.Covered"/>.</summary>
    public string Covered { get; private set; } = CdsCoverageCodes.Covered.IsCovered;

    /// <summary><see cref="CdsCoverageCodes.PaNeeded"/> (never satisfied); null = not stated.</summary>
    public string? PaNeeded { get; private set; }

    /// <summary><see cref="CdsCoverageCodes.DocNeeded"/>; null = no documentation needed.</summary>
    public string? DocNeeded { get; private set; }

    /// <summary><see cref="CdsCoverageCodes.DocPurpose"/> values; only with <see cref="DocNeeded"/>.</summary>
    public List<string> DocPurposes { get; private set; } = [];

    /// <summary><see cref="CdsCoverageCodes.InfoNeeded"/> values; only when something is conditional.</summary>
    public List<string> InfoNeeded { get; private set; } = [];

    /// <summary>Plain-text reason shown to the clinician (no patient data).</summary>
    public string? Reason { get; private set; }

    /// <summary>The payer's policy page (absolute https), added to the card as a link.</summary>
    public string? PolicyUrl { get; private set; }

    /// <summary>The DTR Questionnaire canonical (absolute URL, optionally "|version"); only with <see cref="DocNeeded"/>.</summary>
    public string? QuestionnaireCanonical { get; private set; }

    public DateOnly EffectiveFrom { get; private set; } = DefaultEffectiveFrom;

    public DateOnly? EffectiveTo { get; private set; }

    public bool IsEnabled { get; private set; } = true;

    /// <summary>True when the rule is on and <paramref name="date"/> is within its effective dates.</summary>
    public bool IsEffectiveOn(DateOnly date) =>
        IsEnabled && date >= EffectiveFrom && (EffectiveTo is null || date <= EffectiveTo.Value);

    public void Update(CdsCoverageRuleValues values)
    {
        var errors = Validate(values);
        if (errors.Count > 0)
        {
            throw new BusinessRuleException(string.Join(" ", errors));
        }

        System = CdsCoverageCodes.CodeSystems.Canonical(values.System)!;
        Code = values.Code!.Trim();
        CodePrefixMatch = values.CodePrefixMatch;
        Covered = values.Covered!.Trim();
        PaNeeded = Blank(values.PaNeeded);
        DocNeeded = Blank(values.DocNeeded);
        DocPurposes = Distinct(values.DocPurposes);
        InfoNeeded = Distinct(values.InfoNeeded);
        Reason = Blank(values.Reason);
        PolicyUrl = Blank(values.PolicyUrl);
        QuestionnaireCanonical = Blank(values.QuestionnaireCanonical);
        EffectiveFrom = values.EffectiveFrom ?? DefaultEffectiveFrom;
        EffectiveTo = values.EffectiveTo;
        IsEnabled = values.IsEnabled;
    }

    public void SetEnabled(bool isEnabled) => IsEnabled = isEnabled;

    /// <summary>
    /// Every problem with <paramref name="values"/> as short sentences (empty when valid). Used by the entity and by
    /// the import preview, so a file is checked row by row exactly as a save would be. Applies the CRD invariants:
    /// not-covered has no pa-needed (crd-ci-q2); info-needed needs a conditional value (crd-ci-q3); a questionnaire
    /// needs doc-needed (crd-ci-q1).
    /// </summary>
    public static IReadOnlyList<string> Validate(CdsCoverageRuleValues values)
    {
        var errors = new List<string>();
        var system = CdsCoverageCodes.CodeSystems.Canonical(values.System);
        var code = values.Code?.Trim();
        if (string.IsNullOrEmpty(system))
        {
            errors.Add("The code system is required.");
        }
        else if (system.Length > SystemMaxLength)
        {
            errors.Add($"The code system must be {SystemMaxLength} characters or fewer.");
        }

        if (string.IsNullOrEmpty(code))
        {
            errors.Add("The code is required.");
        }
        else if (code.Length > CodeMaxLength)
        {
            errors.Add($"The code must be {CodeMaxLength} characters or fewer.");
        }

        var covered = values.Covered?.Trim();
        if (string.IsNullOrEmpty(covered) || !CdsCoverageCodes.Covered.All.Contains(covered))
        {
            errors.Add($"Covered must be one of {string.Join(", ", CdsCoverageCodes.Covered.All)}.");
        }

        var pa = Blank(values.PaNeeded);
        if (pa is not null && !CdsCoverageCodes.PaNeeded.ForRules.Contains(pa))
        {
            errors.Add(pa == CdsCoverageCodes.PaNeeded.Satisfied
                ? "Prior authorization 'satisfied' needs a payer-issued authorization id per patient, so a rule cannot assert it."
                : $"Prior authorization must be one of {string.Join(", ", CdsCoverageCodes.PaNeeded.ForRules)} (or blank).");
        }

        if (covered == CdsCoverageCodes.Covered.NotCovered && pa is not null)
        {
            errors.Add("A not-covered rule cannot also state prior authorization.");
        }

        var doc = Blank(values.DocNeeded);
        if (doc is not null && !CdsCoverageCodes.DocNeeded.All.Contains(doc))
        {
            errors.Add($"Documentation needed must be one of {string.Join(", ", CdsCoverageCodes.DocNeeded.All)} (or blank).");
        }

        var purposes = Distinct(values.DocPurposes);
        var badPurpose = purposes.FirstOrDefault(p => !CdsCoverageCodes.DocPurpose.All.Contains(p));
        if (badPurpose is not null)
        {
            errors.Add($"Documentation purpose '{Clip(badPurpose)}' is not one of {string.Join(", ", CdsCoverageCodes.DocPurpose.All)}.");
        }

        if (purposes.Count > 0 && doc is null)
        {
            errors.Add("A documentation purpose needs 'documentation needed' to be set.");
        }

        var info = Distinct(values.InfoNeeded);
        var badInfo = info.FirstOrDefault(i => !CdsCoverageCodes.InfoNeeded.All.Contains(i));
        if (badInfo is not null)
        {
            errors.Add($"Information needed '{Clip(badInfo)}' is not one of {string.Join(", ", CdsCoverageCodes.InfoNeeded.All)}.");
        }

        if (info.Count > 0
            && covered != CdsCoverageCodes.Covered.Conditional
            && pa != CdsCoverageCodes.PaNeeded.Conditional
            && doc != CdsCoverageCodes.DocNeeded.Conditional)
        {
            errors.Add("Information needed is only allowed when covered, prior authorization or documentation is 'conditional'.");
        }

        var reason = Blank(values.Reason);
        if (reason is { Length: > ReasonMaxLength })
        {
            errors.Add($"The reason must be {ReasonMaxLength} characters or fewer.");
        }

        var policyUrl = Blank(values.PolicyUrl);
        if (policyUrl is not null && (policyUrl.Length > UrlMaxLength
                                      || !Uri.TryCreate(policyUrl, UriKind.Absolute, out var policyUri)
                                      || policyUri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add("The policy URL must be an absolute https URL.");
        }

        var questionnaire = Blank(values.QuestionnaireCanonical);
        if (questionnaire is not null)
        {
            var url = questionnaire.Split('|')[0];
            if (questionnaire.Length > UrlMaxLength
                || !Uri.TryCreate(url, UriKind.Absolute, out var questionnaireUri)
                || (questionnaireUri.Scheme != Uri.UriSchemeHttps && questionnaireUri.Scheme != Uri.UriSchemeHttp))
            {
                errors.Add("The questionnaire must be a canonical URL (http(s)://..., optionally |version).");
            }

            if (doc is null)
            {
                errors.Add("A questionnaire needs 'documentation needed' to be set.");
            }
        }

        if (values.EffectiveTo is { } to && to < (values.EffectiveFrom ?? DefaultEffectiveFrom))
        {
            errors.Add("Effective to must be on or after effective from.");
        }

        return errors;
    }

    private static string? Blank(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static List<string> Distinct(IEnumerable<string>? values) =>
        (values ?? []).Select(v => v?.Trim()).Where(v => !string.IsNullOrEmpty(v)).Cast<string>().Distinct(StringComparer.Ordinal).ToList();

    private static string Clip(string value) => value.Length > 40 ? value[..40] : value;
}

/// <summary>The editable values of a <see cref="CdsCoverageRule"/> (admin save and file import).</summary>
public sealed record CdsCoverageRuleValues(
    string? System,
    string? Code,
    bool CodePrefixMatch,
    string? Covered,
    string? PaNeeded,
    string? DocNeeded,
    IReadOnlyList<string>? DocPurposes,
    IReadOnlyList<string>? InfoNeeded,
    string? Reason,
    string? PolicyUrl,
    string? QuestionnaireCanonical,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo,
    bool IsEnabled);
