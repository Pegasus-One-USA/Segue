using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Application.Services.Cds;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace FHIRBridge.Infrastructure.Cds;

/// <summary>
/// Gathers the deployment facts the go-live readiness rules need (public base URL, replay store, issuers,
/// services, licence, rate limit) and runs <see cref="CdsReadinessRules"/>. Reads configuration only; nothing
/// secret (keys, connection strings, tokens) is put into the report.
/// </summary>
public sealed class CdsReadinessService : ICdsReadinessService
{
    /// <summary>The same keys and defaults Program.cs builds the "cds" rate-limit policy from.</summary>
    public const string RateLimitPermitKey = "RateLimiting:Cds:PermitPerWindow";
    public const string RateLimitWindowKey = "RateLimiting:Cds:WindowMinutes";
    public const int DefaultRateLimitPermit = 600;
    public const int DefaultRateLimitWindowMinutes = 1;

    private readonly ICdsServiceInstanceRepository _instances;
    private readonly ICdsTrustedIssuerRepository _issuers;
    private readonly ICdsServiceHandlerRegistry _handlers;
    private readonly ICdsPublicBaseUrlResolver _baseUrlResolver;
    private readonly ISystemSettingsCache _settings;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly CdsReplayGuard _replayGuard;
    private readonly ILicenseService _license;
    private readonly ICdsPayerRepository? _payers;
    private readonly ICdsCoverageRuleRepository? _coverageRules;

    public CdsReadinessService(
        ICdsServiceInstanceRepository instances,
        ICdsTrustedIssuerRepository issuers,
        ICdsServiceHandlerRegistry handlers,
        ICdsPublicBaseUrlResolver baseUrlResolver,
        ISystemSettingsCache settings,
        IConfiguration configuration,
        IHostEnvironment environment,
        CdsReplayGuard replayGuard,
        ILicenseService license,
        ICdsPayerRepository? payers = null,
        ICdsCoverageRuleRepository? coverageRules = null)
    {
        _payers = payers;
        _coverageRules = coverageRules;
        _instances = instances;
        _issuers = issuers;
        _handlers = handlers;
        _baseUrlResolver = baseUrlResolver;
        _settings = settings;
        _configuration = configuration;
        _environment = environment;
        _replayGuard = replayGuard;
        _license = license;
    }

    public async Task<CdsReadinessDto> GetAsync(CancellationToken cancellationToken)
    {
        // No request origin: only a configured Cds:PublicBaseUrl counts here.
        var baseUrl = await _baseUrlResolver.ResolveAsync(string.Empty, cancellationToken);
        var permit = await _settings.GetIntAsync(
            RateLimitPermitKey, _configuration.GetValue<int?>(RateLimitPermitKey) ?? DefaultRateLimitPermit, cancellationToken);
        var windowMinutes = await _settings.GetIntAsync(
            RateLimitWindowKey, _configuration.GetValue<int?>(RateLimitWindowKey) ?? DefaultRateLimitWindowMinutes, cancellationToken);

        var inputs = new CdsReadinessInputs(
            baseUrl.IsConfigured ? baseUrl.BaseUrl : null,
            _environment.IsDevelopment(),
            _replayGuard.IsShared,
            await _issuers.GetAllAsync(cancellationToken),
            await _instances.GetAllAsync(cancellationToken),
            _handlers,
            _license.Current,
            permit,
            windowMinutes,
            _payers is null ? [] : await _payers.GetAllAsync(cancellationToken),
            _coverageRules is null ? [] : await _coverageRules.GetAllAsync(cancellationToken));

        return CdsReadinessRules.Evaluate(inputs, DateTime.UtcNow);
    }
}
