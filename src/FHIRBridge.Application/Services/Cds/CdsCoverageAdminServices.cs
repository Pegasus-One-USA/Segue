using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Application.Services.Cds;

/// <summary>Admin CRUD over coverage requirements payers (api/v1/cds/payers).</summary>
public interface ICdsPayerService
{
    Task<IReadOnlyList<CdsPayerDto>> GetAllAsync(CancellationToken cancellationToken);

    Task<CdsPayerDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<CdsPayerDto> CreateAsync(SaveCdsPayerRequest request, CancellationToken cancellationToken);

    Task<CdsPayerDto> UpdateAsync(Guid id, SaveCdsPayerRequest request, CancellationToken cancellationToken);

    /// <summary>Deletes the payer and all of its rules.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>Admin CRUD, import and export over coverage rules (api/v1/cds/coverage-rules).</summary>
public interface ICdsCoverageRuleService
{
    Task<CdsCoverageRulePageDto> GetPagedAsync(CdsCoverageRuleFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    Task<CdsCoverageRuleDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<CdsCoverageRuleDto> CreateAsync(SaveCdsCoverageRuleRequest request, CancellationToken cancellationToken);

    Task<CdsCoverageRuleDto> UpdateAsync(Guid id, SaveCdsCoverageRuleRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Checks (mode preview: writes nothing) or applies (mode apply: all rows or none) a rule file.
    /// </summary>
    /// <param name="payerId">When set, every row belongs to this payer (the file's payer column may then be blank, or must name it).</param>
    Task<CdsCoverageRuleImportResultDto> ImportAsync(
        string format, string mode, string strategy, Guid? payerId, string? content, CancellationToken cancellationToken);

    Task<CdsCoverageFileDto> ExportAsync(string format, Guid? payerId, CancellationToken cancellationToken);

    CdsCoverageFileDto GetTemplate(string format);
}

/// <summary>Sends a synthetic, non-PHI CRD request to a payer's relay endpoint (api/v1/cds/payers/{id}/test-relay).</summary>
public interface ICdsPayerRelayTestService
{
    /// <exception cref="NotFoundException">No payer with that id.</exception>
    Task<CdsPayerRelayTestResultDto> TestAsync(Guid payerId, CancellationToken cancellationToken);
}

public sealed class CdsPayerService : ICdsPayerService
{
    private readonly ICdsPayerRepository _payers;
    private readonly ICdsCoverageRuleRepository _rules;
    private readonly ICdsCoverageRuleCache _cache;
    private readonly IUserDisplayNameResolver _userDisplayNameResolver;
    private readonly ICdsServiceInstanceRepository _instances;

    public CdsPayerService(
        ICdsPayerRepository payers,
        ICdsCoverageRuleRepository rules,
        ICdsCoverageRuleCache cache,
        IUserDisplayNameResolver userDisplayNameResolver,
        ICdsServiceInstanceRepository instances)
    {
        _payers = payers;
        _rules = rules;
        _cache = cache;
        _userDisplayNameResolver = userDisplayNameResolver;
        _instances = instances;
    }

    public async Task<IReadOnlyList<CdsPayerDto>> GetAllAsync(CancellationToken cancellationToken) =>
        await ToDtosAsync(await _payers.GetAllAsync(cancellationToken), cancellationToken);

    public async Task<CdsPayerDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var payer = await _payers.GetByIdAsync(id, cancellationToken);
        return payer is null ? null : (await ToDtosAsync([payer], cancellationToken))[0];
    }

    public async Task<CdsPayerDto> CreateAsync(SaveCdsPayerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payer = new CdsPayer(
            request.Name, request.Identifiers, request.PlanValues, request.IsEnabled, request.RelayEnabled, request.RelayServiceUrl,
            request.RelayTimeoutMs, request.FallbackToLocalRules, request.ForwardFhirAuthorization, request.RelayClientId);
        await EnsureNameIsFreeAsync(payer.Name, null, cancellationToken);
        await _payers.AddAsync(payer, cancellationToken);
        _cache.Invalidate();
        await CdsCoverageSignOff.ClearAsync(_instances, cancellationToken);
        return (await ToDtosAsync([payer], cancellationToken))[0];
    }

    public async Task<CdsPayerDto> UpdateAsync(Guid id, SaveCdsPayerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payer = await GetRequiredAsync(id, cancellationToken);
        payer.Update(
            request.Name, request.Identifiers, request.PlanValues, request.IsEnabled, request.RelayEnabled, request.RelayServiceUrl,
            request.RelayTimeoutMs, request.FallbackToLocalRules, request.ForwardFhirAuthorization, request.RelayClientId);
        await EnsureNameIsFreeAsync(payer.Name, id, cancellationToken);
        await _payers.UpdateAsync(payer, cancellationToken);
        _cache.Invalidate();
        await CdsCoverageSignOff.ClearAsync(_instances, cancellationToken);
        return (await ToDtosAsync([payer], cancellationToken))[0];
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var payer = await GetRequiredAsync(id, cancellationToken);
        var rules = await _rules.GetByPayerAsync(id, cancellationToken);
        await _payers.DeleteAsync(payer, rules, cancellationToken);
        _cache.Invalidate();
        await CdsCoverageSignOff.ClearAsync(_instances, cancellationToken);
    }

    private async Task EnsureNameIsFreeAsync(string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        if (await _payers.NameExistsAsync(name, excludeId, cancellationToken))
        {
            throw new BusinessRuleException($"A payer named '{name}' already exists.");
        }
    }

    private async Task<CdsPayer> GetRequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await _payers.GetByIdAsync(id, cancellationToken) ?? throw new NotFoundException("CdsPayer", id);

    private async Task<IReadOnlyList<CdsPayerDto>> ToDtosAsync(IReadOnlyList<CdsPayer> payers, CancellationToken cancellationToken)
    {
        var names = await _userDisplayNameResolver.ResolveAsync(payers.SelectMany(x => new[] { x.CreatedBy, x.ModifiedBy }), cancellationToken);
        var rules = await _rules.GetAllAsync(cancellationToken);
        var counts = rules.GroupBy(rule => rule.PayerId)
            .ToDictionary(group => group.Key, group => (All: group.Count(), Enabled: group.Count(rule => rule.IsEnabled)));
        return payers.Select(payer =>
        {
            var count = counts.GetValueOrDefault(payer.Id);
            return new CdsPayerDto(
                payer.Id,
                payer.Name,
                payer.Identifiers.ToArray(),
                payer.PlanValues.ToArray(),
                payer.IsEnabled,
                payer.RelayEnabled,
                payer.RelayServiceUrl,
                payer.RelayTimeoutMs,
                payer.FallbackToLocalRules,
                payer.ForwardFhirAuthorization,
                payer.RelayClientId,
                count.All,
                count.Enabled,
                payer.CreatedOnUtc,
                Resolve(names, payer.CreatedBy),
                payer.ModifiedOnUtc,
                Resolve(names, payer.ModifiedBy));
        }).ToArray();
    }

    internal static string? Resolve(IReadOnlyDictionary<string, string> names, string? actor) =>
        actor is null ? null : names.GetValueOrDefault(actor, actor);
}

public sealed class CdsCoverageRuleService : ICdsCoverageRuleService
{
    private const int MaxPageSize = 200;

    private readonly ICdsCoverageRuleRepository _rules;
    private readonly ICdsPayerRepository _payers;
    private readonly ICdsCoverageRuleCache _cache;
    private readonly IUserDisplayNameResolver _userDisplayNameResolver;
    private readonly ICdsServiceInstanceRepository _instances;

    public CdsCoverageRuleService(
        ICdsCoverageRuleRepository rules,
        ICdsPayerRepository payers,
        ICdsCoverageRuleCache cache,
        IUserDisplayNameResolver userDisplayNameResolver,
        ICdsServiceInstanceRepository instances)
    {
        _rules = rules;
        _payers = payers;
        _cache = cache;
        _userDisplayNameResolver = userDisplayNameResolver;
        _instances = instances;
    }

    public async Task<CdsCoverageRulePageDto> GetPagedAsync(
        CdsCoverageRuleFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var paged = await _rules.GetPagedAsync(filter, page, pageSize, cancellationToken);
        return new CdsCoverageRulePageDto(await ToDtosAsync(paged.Items, cancellationToken), paged.TotalCount, paged.Page, paged.PageSize);
    }

    public async Task<CdsCoverageRuleDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var rule = await _rules.GetByIdAsync(id, cancellationToken);
        return rule is null ? null : (await ToDtosAsync([rule], cancellationToken))[0];
    }

    public async Task<CdsCoverageRuleDto> CreateAsync(SaveCdsCoverageRuleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await RequirePayerAsync(request.PayerId, cancellationToken);
        var rule = new CdsCoverageRule(request.PayerId, Values(request));
        await EnsureKeyIsFreeAsync(rule, null, cancellationToken);
        await _rules.AddAsync(rule, cancellationToken);
        _cache.Invalidate();
        await CdsCoverageSignOff.ClearAsync(_instances, cancellationToken);
        return (await ToDtosAsync([rule], cancellationToken))[0];
    }

    public async Task<CdsCoverageRuleDto> UpdateAsync(Guid id, SaveCdsCoverageRuleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rule = await _rules.GetByIdAsync(id, cancellationToken) ?? throw new NotFoundException("CdsCoverageRule", id);
        if (request.PayerId != rule.PayerId)
        {
            throw new BusinessRuleException("A rule cannot be moved to another payer; delete it and add it to the other payer.");
        }

        rule.Update(Values(request));
        await EnsureKeyIsFreeAsync(rule, id, cancellationToken);
        await _rules.UpdateAsync(rule, cancellationToken);
        _cache.Invalidate();
        await CdsCoverageSignOff.ClearAsync(_instances, cancellationToken);
        return (await ToDtosAsync([rule], cancellationToken))[0];
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var rule = await _rules.GetByIdAsync(id, cancellationToken) ?? throw new NotFoundException("CdsCoverageRule", id);
        await _rules.DeleteAsync(rule, cancellationToken);
        _cache.Invalidate();
        await CdsCoverageSignOff.ClearAsync(_instances, cancellationToken);
    }

    public async Task<CdsCoverageRuleImportResultDto> ImportAsync(
        string format, string mode, string strategy, Guid? payerId, string? content, CancellationToken cancellationToken)
    {
        format = (format ?? string.Empty).Trim().ToLowerInvariant();
        mode = (mode ?? string.Empty).Trim().ToLowerInvariant();
        strategy = string.IsNullOrWhiteSpace(strategy) ? CdsCoverageFileFormats.StrategyMerge : strategy.Trim().ToLowerInvariant();
        if (format is not (CdsCoverageFileFormats.Csv or CdsCoverageFileFormats.Json))
        {
            throw new BusinessRuleException("format must be csv or json.");
        }

        if (mode is not (CdsCoverageFileFormats.ModePreview or CdsCoverageFileFormats.ModeApply))
        {
            throw new BusinessRuleException("mode must be preview or apply.");
        }

        if (strategy is not (CdsCoverageFileFormats.StrategyMerge or CdsCoverageFileFormats.StrategyReplace))
        {
            throw new BusinessRuleException("strategy must be merge or replace.");
        }

        var payers = await _payers.GetAllAsync(cancellationToken);
        CdsPayer? fixedPayer = null;
        if (payerId is { } id)
        {
            fixedPayer = payers.FirstOrDefault(p => p.Id == id) ?? throw new NotFoundException("CdsPayer", id);
        }

        var parsed = CdsCoverageRuleFile.Parse(format, content, payerColumnRequired: fixedPayer is null);
        if (parsed.FileErrors.Count > 0)
        {
            return new CdsCoverageRuleImportResultDto(mode, format, strategy, false, 0, 0, 0, 0, 0, 0, parsed.FileErrors.Count, parsed.FileErrors, []);
        }

        var payersByName = payers.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var existing = await _rules.GetAllAsync(cancellationToken);
        var existingByKey = existing.GroupBy(Key).ToDictionary(group => group.Key, group => group.First());

        var resultRows = new List<CdsCoverageRuleImportRowDto>();
        var seen = new HashSet<(Guid, string, string, DateOnly)>();
        var added = new List<CdsCoverageRule>();
        var updated = new List<CdsCoverageRule>();
        var pendingUpdates = new List<(CdsCoverageRule Rule, CdsCoverageRuleValues Values)>();
        var payersInFile = new HashSet<Guid>();
        int unchanged = 0, errorRows = 0;

        foreach (var row in parsed.Rows)
        {
            var errors = row.Errors.ToList();
            CdsPayer? payer = fixedPayer;
            if (fixedPayer is not null)
            {
                if (row.Payer is not null && !string.Equals(row.Payer, fixedPayer.Name, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"The row names payer '{Clip(row.Payer)}' but the import is for '{fixedPayer.Name}'.");
                }
            }
            else if (row.Payer is null)
            {
                errors.Add("The payer is required.");
            }
            else if (!payersByName.TryGetValue(row.Payer, out payer))
            {
                errors.Add($"No payer is named '{Clip(row.Payer)}'. Add the payer first.");
            }

            errors.AddRange(CdsCoverageRule.Validate(row.Values));
            var from = row.Values.EffectiveFrom ?? CdsCoverageRule.DefaultEffectiveFrom;
            var payerName = payer?.Name ?? row.Payer;
            if (payer is not null && row.Values.System is { } system && row.Values.Code is { } code)
            {
                var key = (payer.Id, SystemKey(system), code.Trim().ToLowerInvariant(), from);
                if (!seen.Add(key))
                {
                    errors.Add("The file has this payer, system, code and effective-from date more than once.");
                }
            }

            if (errors.Count > 0 || payer is null)
            {
                errorRows++;
                resultRows.Add(new CdsCoverageRuleImportRowDto(row.RowNumber, payerName, row.Values.System, row.Values.Code, from, "error", errors));
                continue;
            }

            payersInFile.Add(payer.Id);
            var ruleKey = (payer.Id, SystemKey(row.Values.System!), row.Values.Code!.Trim().ToLowerInvariant(), from);
            if (existingByKey.TryGetValue(ruleKey, out var current))
            {
                if (SameAs(current, row.Values))
                {
                    unchanged++;
                    resultRows.Add(new CdsCoverageRuleImportRowDto(row.RowNumber, payerName, row.Values.System, row.Values.Code, from, "unchanged", []));
                }
                else
                {
                    pendingUpdates.Add((current, row.Values));
                    resultRows.Add(new CdsCoverageRuleImportRowDto(row.RowNumber, payerName, row.Values.System, row.Values.Code, from, "update", []));
                }

                continue;
            }

            added.Add(new CdsCoverageRule(payer.Id, row.Values));
            resultRows.Add(new CdsCoverageRuleImportRowDto(row.RowNumber, payerName, row.Values.System, row.Values.Code, from, "add", []));
        }

        var deleted = new List<CdsCoverageRule>();
        if (strategy == CdsCoverageFileFormats.StrategyReplace)
        {
            IReadOnlySet<Guid> scope = fixedPayer is not null ? new HashSet<Guid> { fixedPayer.Id } : payersInFile;
            foreach (var rule in existing.Where(rule => scope.Contains(rule.PayerId)))
            {
                if (!seen.Contains(Key(rule)))
                {
                    deleted.Add(rule);
                    resultRows.Add(new CdsCoverageRuleImportRowDto(
                        0, payers.FirstOrDefault(p => p.Id == rule.PayerId)?.Name, rule.System, rule.Code, rule.EffectiveFrom, "delete", []));
                }
            }
        }

        var apply = mode == CdsCoverageFileFormats.ModeApply && errorRows == 0;
        if (apply)
        {
            // Only now are the tracked rows changed, so a preview (or an apply with errors) never alters them.
            foreach (var (rule, values) in pendingUpdates)
            {
                rule.Update(values);
                updated.Add(rule);
            }

            await _rules.ApplyAsync(added, updated, deleted, cancellationToken);
            _cache.Invalidate();
            if (added.Count + updated.Count + deleted.Count > 0)
            {
                await CdsCoverageSignOff.ClearAsync(_instances, cancellationToken);
            }
        }

        return new CdsCoverageRuleImportResultDto(
            mode,
            format,
            strategy,
            apply,
            parsed.Rows.Count,
            parsed.Rows.Count - errorRows,
            added.Count,
            pendingUpdates.Count,
            unchanged,
            deleted.Count,
            errorRows,
            [],
            resultRows);
    }

    public async Task<CdsCoverageFileDto> ExportAsync(string format, Guid? payerId, CancellationToken cancellationToken)
    {
        format = CheckFormat(format);
        var payers = await _payers.GetAllAsync(cancellationToken);
        if (payerId is { } id && payers.All(p => p.Id != id))
        {
            throw new NotFoundException("CdsPayer", id);
        }

        var names = payers.ToDictionary(p => p.Id, p => p.Name);
        var rules = (await _rules.GetAllAsync(cancellationToken))
            .Where(rule => names.ContainsKey(rule.PayerId) && (payerId is null || rule.PayerId == payerId))
            .ToArray();
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        return new CdsCoverageFileDto($"segue-coverage-rules-{stamp}.{format}", ContentType(format), CdsCoverageRuleFile.Write(format, rules, names));
    }

    public CdsCoverageFileDto GetTemplate(string format)
    {
        format = CheckFormat(format);
        return new CdsCoverageFileDto($"segue-coverage-rules-template.{format}", ContentType(format), CdsCoverageRuleFile.Template(format));
    }

    private static string CheckFormat(string? format)
    {
        var value = (format ?? CdsCoverageFileFormats.Csv).Trim().ToLowerInvariant();
        return value is CdsCoverageFileFormats.Csv or CdsCoverageFileFormats.Json
            ? value
            : throw new BusinessRuleException("format must be csv or json.");
    }

    private static string ContentType(string format) =>
        format == CdsCoverageFileFormats.Json ? "application/json" : "text/csv";

    private static (Guid, string, string, DateOnly) Key(CdsCoverageRule rule) =>
        (rule.PayerId, SystemKey(rule.System), rule.Code.ToLowerInvariant(), rule.EffectiveFrom);

    /// <summary>The code system as an import key: canonical (an OID alias and its URL are one system), lower-case.</summary>
    private static string SystemKey(string system) => CdsCoverageCodes.CodeSystems.Canonical(system)!.ToLowerInvariant();

    /// <summary>True when saving <paramref name="values"/> would not change <paramref name="rule"/>.</summary>
    private static bool SameAs(CdsCoverageRule rule, CdsCoverageRuleValues values)
    {
        var probe = new CdsCoverageRule(rule.PayerId, values);
        return probe.System == rule.System
               && probe.Code == rule.Code
               && probe.CodePrefixMatch == rule.CodePrefixMatch
               && probe.Covered == rule.Covered
               && probe.PaNeeded == rule.PaNeeded
               && probe.DocNeeded == rule.DocNeeded
               && probe.DocPurposes.SequenceEqual(rule.DocPurposes)
               && probe.InfoNeeded.SequenceEqual(rule.InfoNeeded)
               && probe.Reason == rule.Reason
               && probe.PolicyUrl == rule.PolicyUrl
               && probe.QuestionnaireCanonical == rule.QuestionnaireCanonical
               && probe.EffectiveFrom == rule.EffectiveFrom
               && probe.EffectiveTo == rule.EffectiveTo
               && probe.IsEnabled == rule.IsEnabled;
    }

    private async Task RequirePayerAsync(Guid payerId, CancellationToken cancellationToken)
    {
        if (await _payers.GetByIdAsync(payerId, cancellationToken) is null)
        {
            throw new BusinessRuleException("The payer no longer exists. Refresh and pick again.");
        }
    }

    private async Task EnsureKeyIsFreeAsync(CdsCoverageRule rule, Guid? excludeId, CancellationToken cancellationToken)
    {
        if (await _rules.KeyExistsAsync(rule.PayerId, rule.System, rule.Code, rule.EffectiveFrom, excludeId, cancellationToken))
        {
            throw new BusinessRuleException(
                $"This payer already has a rule for {rule.System}|{rule.Code} effective from {rule.EffectiveFrom:yyyy-MM-dd}.");
        }
    }

    private static CdsCoverageRuleValues Values(SaveCdsCoverageRuleRequest request)
    {
        var codeMatch = string.IsNullOrWhiteSpace(request.CodeMatch) ? CdsCoverageCodes.CodeMatch.Exact : request.CodeMatch.Trim().ToLowerInvariant();
        if (!CdsCoverageCodes.CodeMatch.All.Contains(codeMatch))
        {
            throw new BusinessRuleException("codeMatch must be exact or prefix.");
        }

        return new CdsCoverageRuleValues(
            request.System,
            request.Code,
            codeMatch == CdsCoverageCodes.CodeMatch.Prefix,
            request.Covered,
            request.PaNeeded,
            request.DocNeeded,
            request.DocPurposes,
            request.InfoNeeded,
            request.Reason,
            request.PolicyUrl,
            request.Questionnaire,
            request.EffectiveFrom,
            request.EffectiveTo,
            request.IsEnabled);
    }

    private async Task<IReadOnlyList<CdsCoverageRuleDto>> ToDtosAsync(IReadOnlyList<CdsCoverageRule> rules, CancellationToken cancellationToken)
    {
        var names = await _userDisplayNameResolver.ResolveAsync(rules.SelectMany(x => new[] { x.CreatedBy, x.ModifiedBy }), cancellationToken);
        var payerNames = (await _payers.GetAllAsync(cancellationToken)).ToDictionary(p => p.Id, p => p.Name);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return rules.Select(rule => new CdsCoverageRuleDto(
            rule.Id,
            rule.PayerId,
            payerNames.GetValueOrDefault(rule.PayerId),
            rule.System,
            rule.Code,
            rule.CodePrefixMatch ? CdsCoverageCodes.CodeMatch.Prefix : CdsCoverageCodes.CodeMatch.Exact,
            rule.Covered,
            rule.PaNeeded,
            rule.DocNeeded,
            rule.DocPurposes.ToArray(),
            rule.InfoNeeded.ToArray(),
            rule.Reason,
            rule.PolicyUrl,
            rule.QuestionnaireCanonical,
            rule.EffectiveFrom,
            rule.EffectiveTo,
            rule.IsEnabled,
            rule.IsEffectiveOn(today),
            rule.CreatedOnUtc,
            CdsPayerService.Resolve(names, rule.CreatedBy),
            rule.ModifiedOnUtc,
            CdsPayerService.Resolve(names, rule.ModifiedBy))).ToArray();
    }

    private static string Clip(string value) => value.Length > 60 ? value[..60] : value;
}

/// <summary>
/// A coverage requirements service's clinical sign-off covers the payer rules and payers it answers with, not only
/// its own settings. So any change to a payer or a rule (create, update, delete, an applied import that changes
/// something) clears the sign-off of every coverage requirements service: the new rules have not been reviewed.
/// </summary>
internal static class CdsCoverageSignOff
{
    public static async Task ClearAsync(ICdsServiceInstanceRepository instances, CancellationToken cancellationToken)
    {
        var signedOff = (await instances.GetAllAsync(cancellationToken))
            .Where(instance => instance.HandlerKey == CdsHandlerKeys.CoverageRequirements && instance.IsClinicallySignedOff)
            .ToArray();
        foreach (var instance in signedOff)
        {
            instance.ClearSignOff();
            await instances.UpdateAsync(instance, cancellationToken);
        }
    }
}
