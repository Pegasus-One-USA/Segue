using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Application.Services.Cds;

/// <summary>
/// Builds the Epic setup sheet for one service: every value the health system's Epic team must enter by hand
/// (Epic does not read the discovery endpoint). Configuration only — nothing here comes from a CDS call.
/// </summary>
public sealed class CdsSetupSheetService : ICdsSetupSheetService
{
    private readonly ICdsServiceInstanceRepository _instances;
    private readonly ICdsTrustedIssuerRepository _issuers;
    private readonly ICdsServiceHandlerRegistry _handlers;
    private readonly ICdsPublicBaseUrlResolver _baseUrlResolver;
    private readonly ICdsPayerRepository? _payers;
    private readonly ICdsCoverageRuleRepository? _coverageRules;

    public CdsSetupSheetService(
        ICdsServiceInstanceRepository instances,
        ICdsTrustedIssuerRepository issuers,
        ICdsServiceHandlerRegistry handlers,
        ICdsPublicBaseUrlResolver baseUrlResolver,
        ICdsPayerRepository? payers = null,
        ICdsCoverageRuleRepository? coverageRules = null)
    {
        _instances = instances;
        _issuers = issuers;
        _handlers = handlers;
        _baseUrlResolver = baseUrlResolver;
        _payers = payers;
        _coverageRules = coverageRules;
    }

    public async Task<CdsSetupSheetDto> GetAsync(Guid instanceId, string requestOrigin, CancellationToken cancellationToken)
    {
        var instance = await _instances.GetByIdAsync(instanceId, cancellationToken)
            ?? throw new NotFoundException("CdsService", instanceId);
        var baseUrl = await _baseUrlResolver.ResolveAsync(requestOrigin, cancellationToken);
        var serviceUrl = baseUrl.ServiceUrl(instance.ServiceId);
        var handler = _handlers.Find(instance.HandlerKey);
        var warnings = new List<string>();

        if (!baseUrl.IsConfigured)
        {
            warnings.Add($"{CdsPublicBaseUrl.ConfigurationKey} is not set: the URLs below use this browser's address, and live calls are refused " +
                         "unless an issuer has an audience override. Set it to the address Epic reaches Segue at.");
        }

        if (!instance.IsEnabled)
        {
            warnings.Add("This service is switched off: Epic's calls to it are answered 404 until it is enabled.");
        }

        if (handler is null)
        {
            warnings.Add($"The rule type '{instance.HandlerKey}' is not available in this version of Segue, so the service cannot answer.");
        }

        var allIssuers = await _issuers.GetAllAsync(cancellationToken);
        var issuers = (instance.AllowedIssuerIds.Count == 0
                ? allIssuers
                : allIssuers.Where(issuer => instance.AllowedIssuerIds.Contains(issuer.Id)))
            .OrderBy(issuer => issuer.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        AddIssuerWarnings(issuers, warnings);

        var callbackPossible = instance.FetchMissingData && issuers.Any(issuer => issuer.IsEnabled && issuer.AllowCallback);
        var prefetch = Prefetch(handler, instance.Hook, callbackPossible);
        if (prefetch.Count > 0 && !callbackPossible)
        {
            warnings.Add("Segue will not fetch missing data for this service, so Epic must send every prefetch key below.");
        }

        var fixes = Fixes(instance.GetFixes(), handler);
        if (fixes.Count > 0)
        {
            warnings.Add("One-click fixes are on: the health system must review the codes below and allow the suggestions in its Epic build.");
        }

        if (!instance.IsClinicallySignedOff)
        {
            warnings.Add("Not clinically reviewed: record a clinician's sign-off on the service's setup page before real use.");
        }

        var links = instance.GetLinks();
        if (links.Any(link => link.Type == CdsCardLinkTypes.Smart))
        {
            warnings.Add("Every SMART link below must be on the health system's SMART app allow-list before Epic will launch it.");
        }

        var crd = instance.HandlerKey == CdsHandlerKeys.CoverageRequirements && handler is not null
            ? await CrdAsync(instance, handler, baseUrl, warnings, cancellationToken)
            : null;

        return new CdsSetupSheetDto(
            instance.Id,
            instance.ServiceId,
            instance.Title,
            instance.Hook,
            instance.IsEnabled,
            serviceUrl,
            baseUrl.FeedbackUrl(instance.ServiceId),
            serviceUrl,
            baseUrl.IsConfigured,
            instance.FetchMissingData,
            instance.FixesEnabled,
            prefetch,
            instance.GetOverrideReasons(),
            CdsOverrideReasonOption.System,
            fixes,
            links,
            issuers.Select(issuer => new CdsSetupSheetIssuerDto(
                issuer.Id,
                issuer.Name,
                issuer.Vendor,
                issuer.Issuer,
                issuer.JwksUrl,
                issuer.JwksJson is not null,
                issuer.ExpectedSubject,
                issuer.Audience ?? serviceUrl,
                issuer.EffectiveFhirBaseUrl,
                issuer.AllowCallback,
                issuer.IsEnabled)).ToArray(),
            warnings,
            DateTime.UtcNow,
            instance.IsClinicallySignedOff,
            instance.SignedOffBy,
            instance.SignedOffAtUtc,
            instance.SignOffNote,
            crd);
    }

    /// <summary>What the Epic team needs for a coverage requirements service (Epic's "CRD Request" outgoing API).</summary>
    private async Task<CdsSetupSheetCrdDto> CrdAsync(
        CdsServiceInstance instance, ICdsServiceHandler handler, CdsPublicBaseUrl baseUrl, List<string> warnings, CancellationToken cancellationToken)
    {
        var settings = CdsSettingsValues.Resolve(handler.Settings, instance.SettingsJson);
        var useSystemActions = settings.GetBoolean("useSystemActions", true);
        var dtrUrl = settings.GetText("dtrAppUrl")?.Trim();
        var dtrType = settings.GetText("dtrLinkType", CdsCardLinkTypes.Smart);
        IReadOnlyList<CdsPayer> payers = _payers is null ? [] : await _payers.GetAllAsync(cancellationToken);
        IReadOnlyList<CdsCoverageRule> rules = _coverageRules is null ? [] : await _coverageRules.GetAllAsync(cancellationToken);
        var enabledRules = rules.Where(rule => rule.IsEnabled).GroupBy(rule => rule.PayerId).ToDictionary(g => g.Key, g => g.Count());

        var apis = new List<string>
        {
            "CDS Hooks Framework (CRD Request outgoing call)",
            "Coverage.Search (R4), for the coverage prefetch or for fetching it when it is missing",
        };
        if (useSystemActions)
        {
            apis.Add("ServiceRequest.Update (Unsigned Order) (R4), so coverage information can be added to orders (systemActions, Epic Feb 2024+)");
        }

        if (instance.Hook is CdsHookNames.EncounterStart or CdsHookNames.EncounterDischarge)
        {
            apis.Add("Encounter.Search (R4), for the encounter prefetch");
        }

        var notes = new List<string>
        {
            $"In Epic, point the CRD Request for the {instance.Hook} hook at the service URL above, with the prefetch below (it must include the patient's active Coverage).",
            "MedicationRequests are never updated by Segue: their coverage information is shown as a card.",
        };
        if (!string.IsNullOrEmpty(dtrUrl))
        {
            notes.Add(dtrType == CdsCardLinkTypes.Smart
                ? $"Add the documentation (DTR) SMART app {dtrUrl} to the health system's SMART app allow-list."
                : $"Cards link to the documentation page {dtrUrl}.");
        }

        if (payers.Any(payer => payer.IsEnabled && payer.RelayEnabled))
        {
            notes.Add($"Payers that receive relayed calls verify Segue's signature with its JWKS at {baseUrl.JwksUrl}.");
        }

        if (!payers.Any(payer => payer.IsEnabled))
        {
            warnings.Add("No payer is switched on: coverage requirements calls are answered without coverage information. Add the payers under Coverage requirements.");
        }

        return new CdsSetupSheetCrdDto(
            instance.Hook,
            apis,
            useSystemActions,
            string.IsNullOrEmpty(dtrUrl) ? null : dtrUrl,
            string.IsNullOrEmpty(dtrUrl) ? null : dtrType,
            baseUrl.JwksUrl,
            payers.Select(payer => new CdsSetupSheetPayerDto(
                payer.Id,
                payer.Name,
                payer.IsEnabled,
                payer.Identifiers.ToArray(),
                payer.PlanValues.ToArray(),
                enabledRules.GetValueOrDefault(payer.Id),
                payer.RelayEnabled,
                payer.RelayServiceUrl,
                payer.ForwardFhirAuthorization)).ToArray(),
            notes);
    }

    private static void AddIssuerWarnings(IReadOnlyList<CdsTrustedIssuer> issuers, List<string> warnings)
    {
        if (!issuers.Any(issuer => issuer.IsEnabled))
        {
            warnings.Add("No enabled trusted issuer may call this service yet. Add the health system's Epic as a trusted issuer.");
        }

        foreach (var issuer in issuers.Where(issuer => issuer.IsEnabled))
        {
            if (issuer.JwksUrl is null)
            {
                warnings.Add($"Issuer '{issuer.Name}' has no JWKS URL: Epic names its JWKS URL (jku) in every token, and a token with a jku " +
                             "is refused unless it matches the issuer's configured JWKS URL.");
            }

            if (issuer.ExpectedSubject is null)
            {
                warnings.Add($"Issuer '{issuer.Name}' has no expected sub: enter the health system's CDS client id so only that client is accepted.");
            }
        }
    }

    private static IReadOnlyList<CdsSetupSheetPrefetchDto> Prefetch(ICdsServiceHandler? handler, string hook, bool callbackPossible)
    {
        if (handler is null)
        {
            return [];
        }

        var fallback = handler.GetFallbackQueries(hook);
        return handler.GetPrefetchTemplates(hook)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new CdsSetupSheetPrefetchDto(entry.Key, entry.Value, callbackPossible && fallback.ContainsKey(entry.Key)))
            .ToArray();
    }

    /// <summary>One row per code mapping of each switched-on fix type; one row with no code for a fix that needs none.</summary>
    private static IReadOnlyList<CdsSetupSheetFixDto> Fixes(CdsFixSettings settings, ICdsServiceHandler? handler)
    {
        if (!settings.Enabled)
        {
            return [];
        }

        var rows = new List<CdsSetupSheetFixDto>();
        foreach (var fixType in settings.FixTypes.Where(fixType => fixType.Enabled))
        {
            var descriptor = handler?.SupportedFixTypes.FirstOrDefault(d => string.Equals(d.Key, fixType.Key, StringComparison.Ordinal));
            var label = descriptor?.Label ?? fixType.Key;
            var actionType = descriptor?.ActionType ?? string.Empty;
            var resourceType = descriptor?.ResourceType ?? string.Empty;
            var recommended = string.Equals(settings.RecommendedFixType, fixType.Key, StringComparison.Ordinal);

            if (fixType.Mappings.Count == 0)
            {
                rows.Add(new CdsSetupSheetFixDto(fixType.Key, label, actionType, resourceType, null, null, null, null, recommended));
                continue;
            }

            rows.AddRange(fixType.Mappings.Select(mapping => new CdsSetupSheetFixDto(
                fixType.Key, label, actionType, resourceType, mapping.Trigger, mapping.System, mapping.Code, mapping.Display, recommended,
                mapping.Trigger is null
                    ? null
                    : descriptor?.Triggers?.FirstOrDefault(t => string.Equals(t.Value, mapping.Trigger, StringComparison.Ordinal))?.Label)));
        }

        return rows;
    }
}
