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

    public CdsSetupSheetService(
        ICdsServiceInstanceRepository instances,
        ICdsTrustedIssuerRepository issuers,
        ICdsServiceHandlerRegistry handlers,
        ICdsPublicBaseUrlResolver baseUrlResolver)
    {
        _instances = instances;
        _issuers = issuers;
        _handlers = handlers;
        _baseUrlResolver = baseUrlResolver;
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

        var links = instance.GetLinks();
        if (links.Any(link => link.Type == CdsCardLinkTypes.Smart))
        {
            warnings.Add("Every SMART link below must be on the health system's SMART app allow-list before Epic will launch it.");
        }

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
            DateTime.UtcNow);
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
