using System.Globalization;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Services.Cds;

/// <summary>Everything the go-live readiness rules look at, gathered by the readiness service.</summary>
/// <param name="PublicBaseUrl">The configured Cds:PublicBaseUrl (System Settings, then config); null when unset.</param>
/// <param name="IsDevelopment">The host runs in the Development environment (http is then only a warning).</param>
/// <param name="ReplayProtectionShared">The jti replay guard uses Redis (shared by every node), not process memory.</param>
/// <param name="RateLimitPermitPerWindow">RateLimiting:Cds:PermitPerWindow as configured.</param>
/// <param name="RateLimitWindowMinutes">RateLimiting:Cds:WindowMinutes as configured.</param>
public sealed record CdsReadinessInputs(
    string? PublicBaseUrl,
    bool IsDevelopment,
    bool ReplayProtectionShared,
    IReadOnlyList<CdsTrustedIssuer> Issuers,
    IReadOnlyList<CdsServiceInstance> Services,
    ICdsServiceHandlerRegistry Handlers,
    LicenseStatus License,
    int RateLimitPermitPerWindow,
    int RateLimitWindowMinutes,
    IReadOnlyList<CdsPayer>? Payers = null,
    IReadOnlyList<CdsCoverageRule>? CoverageRules = null);

/// <summary>
/// The go-live readiness rules (pure: no I/O). Each rule gives one check with ok / warning / blocker, a short
/// detail and how to fix it. Details may name services and issuers (configuration) but never hold secrets.
/// Clinical sign-off is advisory: missing sign-off is a warning, never a blocker.
/// </summary>
public static class CdsReadinessRules
{
    /// <summary>Below this many calls per minute per client IP, a busy EHR may be refused (429).</summary>
    public const int MinimumCallsPerMinute = 60;

    /// <summary>A licence that ends within this many days is a warning.</summary>
    public const int LicenseExpiryWarningDays = 30;

    private const int MaxNamesListed = 5;

    public static CdsReadinessDto Evaluate(CdsReadinessInputs inputs, DateTime nowUtc)
    {
        var checks = new List<CdsReadinessCheckDto>
        {
            PublicBaseUrl(inputs),
            ReplayProtection(inputs),
            TrustedIssuer(inputs),
        };

        var enabledIssuers = inputs.Issuers.Where(issuer => issuer.IsEnabled).OrderBy(issuer => issuer.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (enabledIssuers.Length > 0)
        {
            checks.Add(IssuerJwksUrl(enabledIssuers));
            checks.Add(IssuerSubject(enabledIssuers));
            checks.Add(IssuerFhirBaseUrl(enabledIssuers));
        }

        var services = inputs.Services.OrderBy(service => service.Title, StringComparer.OrdinalIgnoreCase).ToArray();
        checks.Add(EnabledService(services));
        checks.Add(FixesSignOff(services));
        checks.Add(SampleDataSignOff(services, inputs.Handlers));
        checks.Add(License(inputs.License));
        checks.Add(RateLimit(inputs.RateLimitPermitPerWindow, inputs.RateLimitWindowMinutes));

        var payers = (inputs.Payers ?? []).OrderBy(payer => payer.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var crdServices = services.Where(service => service.HandlerKey == CdsHandlerKeys.CoverageRequirements).ToArray();
        if (crdServices.Length > 0 || payers.Length > 0)
        {
            var rules = inputs.CoverageRules ?? [];
            checks.Add(CrdPayerCoverage(payers, rules, crdServices, DateOnly.FromDateTime(nowUtc)));
            checks.Add(CrdRelayHttps(payers));
            checks.Add(CrdRulesExpiring(payers, rules, DateOnly.FromDateTime(nowUtc)));
            checks.Add(CrdSignOff(crdServices));
        }

        return new CdsReadinessDto(Worst(checks), checks, nowUtc);
    }

    private static CdsReadinessCheckDto PublicBaseUrl(CdsReadinessInputs inputs)
    {
        const string label = "Public address (Cds:PublicBaseUrl)";
        var key = CdsReadinessCheckKeys.PublicBaseUrl;
        var value = inputs.PublicBaseUrl?.Trim();

        if (string.IsNullOrEmpty(value))
        {
            return new(key, label, CdsReadinessStatus.Blocker,
                $"{CdsPublicBaseUrl.ConfigurationKey} is not set, so every live CDS call is refused: the token's audience can only be checked against it.",
                $"Set {CdsPublicBaseUrl.ConfigurationKey} (System Settings or the Cds__PublicBaseUrl environment variable) to the https address Epic reaches Segue at, e.g. https://segue.hospital.org.");
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return new(key, label, CdsReadinessStatus.Blocker,
                $"{CdsPublicBaseUrl.ConfigurationKey} ({value}) is not a full web address.",
                "Enter the address as https://host[/path], with no query string.");
        }

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            return inputs.IsDevelopment
                ? new(key, label, CdsReadinessStatus.Warning,
                    $"{value} uses plain http. Accepted in Development only; Epic calls a CDS service over https.",
                    "Before go-live, deploy Segue behind https and set the https address.")
                : new(key, label, CdsReadinessStatus.Blocker,
                    $"{value} uses plain http. EHR tokens and patient data would travel unencrypted, and Epic calls CDS services over https only.",
                    "Deploy Segue behind https (TLS certificate on the public host) and set the https address.");
        }

        return new(key, label, CdsReadinessStatus.Ok, $"Services are published under {value.TrimEnd('/')}/cds-services.", null);
    }

    private static CdsReadinessCheckDto ReplayProtection(CdsReadinessInputs inputs) =>
        inputs.ReplayProtectionShared
            ? new(CdsReadinessCheckKeys.ReplayProtection, "Replay protection (Redis)", CdsReadinessStatus.Ok,
                "Each EHR token is accepted once, checked in Redis, so every Segue node shares the record.", null)
            : new(CdsReadinessCheckKeys.ReplayProtection, "Replay protection (Redis)", CdsReadinessStatus.Warning,
                "Used tokens are remembered in this server's memory only. That protects a single node; with more than one node, or after a restart, a captured token could be replayed.",
                "Set ConnectionStrings:Redis (with ssl=true) so every node shares one replay record.");

    private static CdsReadinessCheckDto TrustedIssuer(CdsReadinessInputs inputs)
    {
        const string label = "Trusted EHR issuer";
        var enabled = inputs.Issuers.Count(issuer => issuer.IsEnabled);
        if (enabled == 0)
        {
            return new(CdsReadinessCheckKeys.TrustedIssuer, label, CdsReadinessStatus.Blocker,
                inputs.Issuers.Count == 0
                    ? "No trusted issuer is registered, so every EHR call is refused."
                    : "Every trusted issuer is switched off, so every EHR call is refused.",
                "Add the health system's Epic under Connect your EHR > Trusted issuers: iss = the organisation's FHIR base URL, the JWKS URL its tokens name in jku, and its CDS client id as the expected sub.");
        }

        return new(CdsReadinessCheckKeys.TrustedIssuer, label, CdsReadinessStatus.Ok,
            enabled == 1 ? "1 trusted issuer is on." : $"{enabled.ToString(CultureInfo.InvariantCulture)} trusted issuers are on.", null);
    }

    /// <summary>
    /// Epic names its JWKS URL (jku) in every token and a jku must match the issuer's configured URL, so an Epic
    /// issuer with pasted keys only refuses every call (blocker). Other EHRs may sign without a jku (warning).
    /// </summary>
    private static CdsReadinessCheckDto IssuerJwksUrl(IReadOnlyList<CdsTrustedIssuer> enabledIssuers)
    {
        const string label = "Issuer JWKS URL (jku)";
        var missing = enabledIssuers.Where(issuer => issuer.JwksUrl is null).ToArray();
        if (missing.Length == 0)
        {
            return new(CdsReadinessCheckKeys.IssuerJwksUrl, label, CdsReadinessStatus.Ok, "Every enabled issuer has a JWKS URL.", null);
        }

        var epicMissing = missing.Any(IsEpic);
        return new(CdsReadinessCheckKeys.IssuerJwksUrl, label, epicMissing ? CdsReadinessStatus.Blocker : CdsReadinessStatus.Warning,
            $"No JWKS URL on: {Names(missing.Select(issuer => issuer.Name))}. A token that names a jku is refused unless it matches the issuer's JWKS URL"
            + (epicMissing ? ", and Epic always sends one." : "."),
            "Edit the issuer and enter the JWKS URL the EHR's tokens name in their jku header (Epic gives it with the CDS client registration).");
    }

    private static CdsReadinessCheckDto IssuerSubject(IReadOnlyList<CdsTrustedIssuer> enabledIssuers)
    {
        const string label = "Issuer client id (sub)";
        var missing = enabledIssuers.Where(issuer => issuer.ExpectedSubject is null).ToArray();
        return missing.Length == 0
            ? new(CdsReadinessCheckKeys.IssuerSubject, label, CdsReadinessStatus.Ok, "Every enabled issuer names the client id (sub) it must sign with.", null)
            : new(CdsReadinessCheckKeys.IssuerSubject, label, CdsReadinessStatus.Warning,
                $"No expected sub on: {Names(missing.Select(issuer => issuer.Name))}. Any client of that EHR signing with the same iss would be accepted.",
                "Edit the issuer and enter the health system's CDS client id as the expected sub.");
    }

    /// <summary>The FHIR server missing data is fetched from: FhirBaseUrl, else iss (Epic's iss is its FHIR base).</summary>
    private static CdsReadinessCheckDto IssuerFhirBaseUrl(IReadOnlyList<CdsTrustedIssuer> enabledIssuers)
    {
        const string label = "Issuer FHIR base URL";
        var unusable = enabledIssuers
            .Where(issuer => issuer.AllowCallback && !IsHttpsUrl(issuer.EffectiveFhirBaseUrl))
            .ToArray();
        return unusable.Length == 0
            ? new(CdsReadinessCheckKeys.IssuerFhirBaseUrl, label, CdsReadinessStatus.Ok,
                "Every enabled issuer that allows fetching missing data has an https FHIR base URL (its own, or its iss).", null)
            : new(CdsReadinessCheckKeys.IssuerFhirBaseUrl, label, CdsReadinessStatus.Warning,
                $"Missing data cannot be fetched from: {Names(unusable.Select(issuer => issuer.Name))}. Their FHIR base URL (or iss, when it is blank) is not an https address.",
                "Edit the issuer and enter the https FHIR base URL (for Epic, normally the same as iss, e.g. https://host/api/FHIR/R4), or switch off fetching missing data and have Epic send every prefetch key.");
    }

    private static CdsReadinessCheckDto EnabledService(IReadOnlyList<CdsServiceInstance> services)
    {
        const string label = "Service switched on";
        var enabled = services.Count(service => service.IsEnabled);
        return enabled == 0
            ? new(CdsReadinessCheckKeys.EnabledService, label, CdsReadinessStatus.Blocker,
                services.Count == 0 ? "No CDS service has been added yet." : "Every CDS service is switched off, so every call is answered 404.",
                "Under Services, add a service (or open one) and switch it on. Go live with one service at a time and watch Activity.")
            : new(CdsReadinessCheckKeys.EnabledService, label, CdsReadinessStatus.Ok,
                enabled == 1 ? "1 service is on." : $"{enabled.ToString(CultureInfo.InvariantCulture)} services are on.", null);
    }

    private static CdsReadinessCheckDto FixesSignOff(IReadOnlyList<CdsServiceInstance> services)
    {
        const string label = "Clinical sign-off: one-click fixes";
        var withFixes = services.Where(service => service.FixesEnabled).ToArray();
        if (withFixes.Length == 0)
        {
            return new(CdsReadinessCheckKeys.FixesSignOff, label, CdsReadinessStatus.Ok, "No service offers one-click fixes.", null);
        }

        var notReviewed = withFixes.Where(service => !service.IsClinicallySignedOff).ToArray();
        return notReviewed.Length == 0
            ? new(CdsReadinessCheckKeys.FixesSignOff, label, CdsReadinessStatus.Ok, "Every service that changes EHR drafts has a clinical sign-off.", null)
            : new(CdsReadinessCheckKeys.FixesSignOff, label, CdsReadinessStatus.Warning,
                $"These services change EHR drafts but have no clinical sign-off: {Names(notReviewed.Select(service => service.Title))}.",
                "Have a clinician review the fix codes with the health system's Epic team, then record the sign-off on each service's setup page.");
    }

    private static CdsReadinessCheckDto SampleDataSignOff(IReadOnlyList<CdsServiceInstance> services, ICdsServiceHandlerRegistry handlers)
    {
        const string label = "Clinical sign-off: sample rules";
        var sample = services.Where(service => handlers.Find(service.HandlerKey)?.UsesSampleReferenceData == true).ToArray();
        if (sample.Length == 0)
        {
            return new(CdsReadinessCheckKeys.SampleDataSignOff, label, CdsReadinessStatus.Ok, "No service uses the sample rules shipped with Segue.", null);
        }

        var notReviewed = sample.Where(service => !service.IsClinicallySignedOff).ToArray();
        return notReviewed.Length == 0
            ? new(CdsReadinessCheckKeys.SampleDataSignOff, label, CdsReadinessStatus.Ok, "Every service on the sample rules has a clinical sign-off.", null)
            : new(CdsReadinessCheckKeys.SampleDataSignOff, label, CdsReadinessStatus.Warning,
                $"These services use the SAMPLE rules shipped with Segue and have no clinical sign-off: {Names(notReviewed.Select(service => service.Title))}.",
                "Have the clinical team review each service's rules, settings, dismiss reasons and links, then record the sign-off on its setup page.");
    }

    private static CdsReadinessCheckDto License(LicenseStatus license)
    {
        const string label = "Licence";
        var key = CdsReadinessCheckKeys.License;
        var expires = license.ExpiresUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return license.State switch
        {
            LicenseState.Active when license.DaysRemaining is { } days && days <= LicenseExpiryWarningDays =>
                new(key, label, CdsReadinessStatus.Warning,
                    $"The licence is active but ends on {expires}. When it ends, every CDS call is refused (503).",
                    "Renew the licence on the License screen before it ends."),
            LicenseState.Active =>
                new(key, label, CdsReadinessStatus.Ok, expires is null ? "The licence is active." : $"The licence is active until {expires}.", null),
            LicenseState.Grace =>
                new(key, label, CdsReadinessStatus.Warning, "The licence has ended and is in its grace period.", "Apply a renewed licence on the License screen."),
            _ =>
                new(key, label, CdsReadinessStatus.Blocker,
                    "No active licence: every CDS call is refused (503).",
                    "Apply a valid licence on the License screen."),
        };
    }

    private static CdsReadinessCheckDto RateLimit(int permitPerWindow, int windowMinutes)
    {
        const string label = "Rate limit";
        var key = CdsReadinessCheckKeys.RateLimit;
        const string howToSet = "Set RateLimiting:Cds:PermitPerWindow and RateLimiting:Cds:WindowMinutes (System Settings or configuration), then restart the API.";
        if (permitPerWindow <= 0 || windowMinutes <= 0)
        {
            return new(key, label, CdsReadinessStatus.Blocker,
                "The CDS rate limit is not a positive number of calls per window.", howToSet);
        }

        var perMinute = (double)permitPerWindow / windowMinutes;
        var detail = $"{permitPerWindow.ToString(CultureInfo.InvariantCulture)} calls per client IP every "
            + (windowMinutes == 1 ? "minute" : $"{windowMinutes.ToString(CultureInfo.InvariantCulture)} minutes")
            + ". A change takes effect after an API restart.";
        return perMinute < MinimumCallsPerMinute
            ? new(key, label, CdsReadinessStatus.Warning,
                detail + $" Epic calls from a few server addresses on every chart open and order, so fewer than {MinimumCallsPerMinute.ToString(CultureInfo.InvariantCulture)} a minute may refuse real traffic (429).",
                howToSet)
            : new(key, label, CdsReadinessStatus.Ok, detail, null);
    }

    /// <summary>Within this many days a coverage rule's end date is a warning.</summary>
    public const int RuleExpiryWarningDays = 30;

    private static CdsReadinessCheckDto CrdPayerCoverage(
        IReadOnlyList<CdsPayer> payers, IReadOnlyList<CdsCoverageRule> rules, IReadOnlyList<CdsServiceInstance> crdServices, DateOnly today)
    {
        const string label = "Coverage requirements: payers";
        var key = CdsReadinessCheckKeys.CrdPayerCoverage;
        var enabled = payers.Where(payer => payer.IsEnabled).ToArray();
        if (enabled.Length == 0)
        {
            return new(key, label, crdServices.Any(service => service.IsEnabled) ? CdsReadinessStatus.Blocker : CdsReadinessStatus.Warning,
                "No payer is switched on, so every coverage requirements call is answered without coverage information.",
                "On the Coverage (CRD) tab, under Payers, add each payer with the identifiers its Coverage.payor carries, then add its rules or its CRD relay.");
        }

        // A rule counts only when it is in effect today: a payer whose enabled rules have all ended, or have not
        // started yet, matches nothing on a call.
        var withRules = rules.Where(rule => rule.IsEffectiveOn(today)).Select(rule => rule.PayerId).ToHashSet();
        var empty = enabled.Where(payer => !payer.UsesRelay && !withRules.Contains(payer.Id)).ToArray();
        return empty.Length == 0
            ? new(key, label, CdsReadinessStatus.Ok, "Every enabled payer has rules in effect today or relays to its own CRD service.", null)
            : new(key, label, CdsReadinessStatus.Warning,
                $"These payers have no rule in effect today and no relay, so their patients get no coverage information: {Names(empty.Select(payer => payer.Name))}.",
                "Add or import the payer's coverage rules (check their start and end dates), or switch on its relay to the payer's own CRD service.");
    }

    private static CdsReadinessCheckDto CrdRelayHttps(IReadOnlyList<CdsPayer> payers)
    {
        const string label = "Coverage requirements: payer relay";
        var key = CdsReadinessCheckKeys.CrdRelayHttps;
        var relaying = payers.Where(payer => payer.IsEnabled && payer.RelayEnabled).ToArray();
        if (relaying.Length == 0)
        {
            return new(key, label, CdsReadinessStatus.Ok, "No payer relays to its own CRD service.", null);
        }

        var insecure = relaying.Where(payer => !CdsPayer.IsHttpsUrl(payer.RelayServiceUrl)).ToArray();
        if (insecure.Length > 0)
        {
            return new(key, label, CdsReadinessStatus.Blocker,
                $"The relay address of these payers is not an https URL, so their calls are not relayed: {Names(insecure.Select(payer => payer.Name))}.",
                "Edit the payer and enter its CRD service's https address.");
        }

        var forwarding = relaying.Where(payer => payer.ForwardFhirAuthorization).ToArray();
        return forwarding.Length == 0
            ? new(key, label, CdsReadinessStatus.Ok, "Every relay uses https and none forwards the EHR's access token.", null)
            : new(key, label, CdsReadinessStatus.Warning,
                $"These payers receive the EHR's FHIR access token with each relayed call: {Names(forwarding.Select(payer => payer.Name))}.",
                "Keep token forwarding only for payers whose agreement with the health system covers reading its FHIR server; otherwise switch it off.");
    }

    private static CdsReadinessCheckDto CrdRulesExpiring(IReadOnlyList<CdsPayer> payers, IReadOnlyList<CdsCoverageRule> rules, DateOnly today)
    {
        const string label = "Coverage requirements: rules ending soon";
        var key = CdsReadinessCheckKeys.CrdRulesExpiring;
        var enabledPayers = payers.Where(payer => payer.IsEnabled).ToDictionary(payer => payer.Id, payer => payer.Name);
        var ending = rules
            .Where(rule => rule.IsEnabled && enabledPayers.ContainsKey(rule.PayerId)
                           && rule.EffectiveTo is { } to && to >= today && to <= today.AddDays(RuleExpiryWarningDays))
            .ToArray();
        if (ending.Length == 0)
        {
            return new(key, label, CdsReadinessStatus.Ok, $"No enabled rule ends within {RuleExpiryWarningDays.ToString(CultureInfo.InvariantCulture)} days.", null);
        }

        var byPayer = ending.GroupBy(rule => enabledPayers[rule.PayerId])
            .Select(group => $"{group.Key} ({group.Count().ToString(CultureInfo.InvariantCulture)})");
        return new(key, label, CdsReadinessStatus.Warning,
            $"{ending.Length.ToString(CultureInfo.InvariantCulture)} enabled rule(s) end within {RuleExpiryWarningDays.ToString(CultureInfo.InvariantCulture)} days: {Names(byPayer)}.",
            "Import the payer's updated rules (with the new effective dates) before the old ones end.");
    }

    private static CdsReadinessCheckDto CrdSignOff(IReadOnlyList<CdsServiceInstance> crdServices)
    {
        const string label = "Clinical sign-off: coverage requirements";
        var key = CdsReadinessCheckKeys.CrdSignOff;
        if (crdServices.Count == 0)
        {
            return new(key, label, CdsReadinessStatus.Ok, "No coverage requirements service has been added.", null);
        }

        var notReviewed = crdServices.Where(service => !service.IsClinicallySignedOff).ToArray();
        return notReviewed.Length == 0
            ? new(key, label, CdsReadinessStatus.Ok, "Every coverage requirements service has a sign-off.", null)
            : new(key, label, CdsReadinessStatus.Warning,
                $"These coverage requirements services have no sign-off: {Names(notReviewed.Select(service => service.Title))}.",
                "Have the clinical and revenue-cycle teams review the payer rules and the cards with the Epic team, then record the sign-off on the service's setup page.");
    }

    private static string Worst(IEnumerable<CdsReadinessCheckDto> checks)
    {
        var statuses = checks.Select(check => check.Status).ToArray();
        if (statuses.Contains(CdsReadinessStatus.Blocker))
        {
            return CdsReadinessStatus.Blocker;
        }

        return statuses.Contains(CdsReadinessStatus.Warning) ? CdsReadinessStatus.Warning : CdsReadinessStatus.Ok;
    }

    private static bool IsEpic(CdsTrustedIssuer issuer) =>
        issuer.Vendor.Contains("epic", StringComparison.OrdinalIgnoreCase);

    private static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static string Names(IEnumerable<string> names)
    {
        var all = names.ToArray();
        var shown = string.Join(", ", all.Take(MaxNamesListed).Select(name => $"\"{name}\""));
        return all.Length > MaxNamesListed
            ? $"{shown} and {(all.Length - MaxNamesListed).ToString(CultureInfo.InvariantCulture)} more"
            : shown;
    }
}
