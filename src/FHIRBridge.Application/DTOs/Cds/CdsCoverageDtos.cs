namespace FHIRBridge.Application.DTOs.Cds;

// Admin API shapes for coverage requirements (Da Vinci CRD): payers, their coverage rules, rule file import /
// export and the payer relay test (api/v1/cds/payers, api/v1/cds/coverage-rules). Payer configuration only —
// never patient data. Dates are "yyyy-MM-dd".

/// <param name="Identifiers">Matched against Coverage.payor: "value", "system|value", or the payer name as the EHR shows it.</param>
/// <param name="PlanValues">Optional Coverage.class values; when not empty one must be on the Coverage.</param>
/// <param name="RelayClientId">The JWT sub Segue signs with for this payer (the client id the payer gave Segue); null = none.</param>
/// <param name="RuleCount">Live rules of this payer (enabled or not).</param>
/// <param name="EnabledRuleCount">Enabled rules.</param>
public sealed record CdsPayerDto(
    Guid Id,
    string Name,
    IReadOnlyList<string> Identifiers,
    IReadOnlyList<string> PlanValues,
    bool IsEnabled,
    bool RelayEnabled,
    string? RelayServiceUrl,
    int RelayTimeoutMs,
    bool FallbackToLocalRules,
    bool ForwardFhirAuthorization,
    string? RelayClientId,
    int RuleCount,
    int EnabledRuleCount,
    DateTime CreatedOnUtc,
    string? CreatedBy,
    DateTime? ModifiedOnUtc,
    string? ModifiedBy);

/// <summary>Create and update body for a payer (PUT replaces every field).</summary>
/// <param name="Identifiers">At least one (each ≤ 200 chars, at most 20).</param>
/// <param name="PlanValues">At most 50.</param>
/// <param name="RelayServiceUrl">Absolute https, no query; required when <paramref name="RelayEnabled"/>.</param>
/// <param name="RelayTimeoutMs">100-10000, default 1000.</param>
/// <param name="FallbackToLocalRules">Default true.</param>
/// <param name="ForwardFhirAuthorization">Default false: the EHR's token is not passed to the payer.</param>
public sealed record SaveCdsPayerRequest(
    string Name,
    IReadOnlyList<string>? Identifiers,
    IReadOnlyList<string>? PlanValues,
    bool IsEnabled,
    bool RelayEnabled = false,
    string? RelayServiceUrl = null,
    int RelayTimeoutMs = 1000,
    bool FallbackToLocalRules = true,
    bool ForwardFhirAuthorization = false,
    string? RelayClientId = null);

/// <param name="CodeMatch">"exact" or "prefix".</param>
/// <param name="Covered">covered | not-covered | conditional.</param>
/// <param name="PaNeeded">no-auth | auth-needed | performpa | conditional | null.</param>
/// <param name="DocNeeded">clinical | admin | both | conditional | null.</param>
/// <param name="DocPurposes">withpa | withclaim | withorder | retain-doc.</param>
/// <param name="InfoNeeded">performer | location | timeframe.</param>
/// <param name="Questionnaire">DTR Questionnaire canonical (url or url|version); null when none.</param>
/// <param name="IsEffectiveToday">Enabled and today (UTC) is within the effective dates.</param>
public sealed record CdsCoverageRuleDto(
    Guid Id,
    Guid PayerId,
    string? PayerName,
    string System,
    string Code,
    string CodeMatch,
    string Covered,
    string? PaNeeded,
    string? DocNeeded,
    IReadOnlyList<string> DocPurposes,
    IReadOnlyList<string> InfoNeeded,
    string? Reason,
    string? PolicyUrl,
    string? Questionnaire,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    bool IsEnabled,
    bool IsEffectiveToday,
    DateTime CreatedOnUtc,
    string? CreatedBy,
    DateTime? ModifiedOnUtc,
    string? ModifiedBy);

/// <summary>Create and update body for a rule (PUT replaces every field).</summary>
/// <param name="EffectiveFrom">Null means 2000-01-01 ("always").</param>
public sealed record SaveCdsCoverageRuleRequest(
    Guid PayerId,
    string System,
    string Code,
    string? CodeMatch,
    string Covered,
    string? PaNeeded,
    string? DocNeeded,
    IReadOnlyList<string>? DocPurposes,
    IReadOnlyList<string>? InfoNeeded,
    string? Reason,
    string? PolicyUrl,
    string? Questionnaire,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo,
    bool IsEnabled = true);

public sealed record CdsCoverageRulePageDto(
    IReadOnlyList<CdsCoverageRuleDto> Items,
    int TotalCount,
    int Page,
    int PageSize);

/// <summary>Values accepted by the import / export endpoints.</summary>
public static class CdsCoverageFileFormats
{
    public const string Csv = "csv";
    public const string Json = "json";

    public const string ModePreview = "preview";
    public const string ModeApply = "apply";

    /// <summary>Add new rules and update matching ones; other rules of the payer are kept.</summary>
    public const string StrategyMerge = "merge";

    /// <summary>The file becomes the payer's whole rule set: rules of a payer named in the file that are not in it are deleted.</summary>
    public const string StrategyReplace = "replace";

    /// <summary>The CSV header, in this order (import accepts any order, case-insensitive).</summary>
    public static readonly IReadOnlyList<string> CsvColumns =
    [
        "payer", "system", "code", "codeMatch", "covered", "paNeeded", "docNeeded", "docPurpose", "infoNeeded",
        "reason", "policyUrl", "questionnaire", "effectiveFrom", "effectiveTo", "enabled",
    ];
}

/// <summary>One row of an import, as checked.</summary>
/// <param name="RowNumber">CSV: the line's record number (header = 1, first rule = 2). JSON: the rule's 1-based position.</param>
/// <param name="Action">add | update | unchanged | delete | error.</param>
/// <param name="Errors">Why the row cannot be imported (empty unless Action is "error").</param>
public sealed record CdsCoverageRuleImportRowDto(
    int RowNumber,
    string? Payer,
    string? System,
    string? Code,
    DateOnly? EffectiveFrom,
    string Action,
    IReadOnlyList<string> Errors);

/// <summary>The result of POST api/v1/cds/coverage-rules/import (preview writes nothing; apply is all-or-nothing).</summary>
/// <param name="Applied">True only for mode "apply" with no errors (the changes were saved).</param>
/// <param name="FileErrors">Problems with the file as a whole (unreadable, missing columns, too many rows).</param>
/// <param name="Rows">Every rule row with its action, plus one "delete" row per rule a replace would remove.</param>
public sealed record CdsCoverageRuleImportResultDto(
    string Mode,
    string Format,
    string Strategy,
    bool Applied,
    int TotalRows,
    int ValidRows,
    int Added,
    int Updated,
    int Unchanged,
    int Deleted,
    int ErrorCount,
    IReadOnlyList<string> FileErrors,
    IReadOnlyList<CdsCoverageRuleImportRowDto> Rows);

/// <summary>A file to download (export or template).</summary>
public sealed record CdsCoverageFileDto(string FileName, string ContentType, string Content);

/// <summary>The result of POST api/v1/cds/payers/{id}/test-relay: a synthetic, non-PHI CRD request to the payer.</summary>
/// <param name="Reachable">True when the payer answered 2xx with a valid CDS Hooks response.</param>
/// <param name="Outcome">"ok" or the short failure reason ("timed out", "HTTP 401", "unreachable", ...).</param>
/// <param name="Message">One sentence for the admin.</param>
public sealed record CdsPayerRelayTestResultDto(
    Guid PayerId,
    string? RelayServiceUrl,
    bool Reachable,
    int? HttpStatus,
    long DurationMs,
    int CardCount,
    int SystemActionCount,
    string Outcome,
    string Message);
