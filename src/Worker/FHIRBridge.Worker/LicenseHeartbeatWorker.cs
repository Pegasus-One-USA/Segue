using System.Net.Http.Json;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Application.Abstractions.Security;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Worker;

/// <summary>
/// Best-effort "phone home" heartbeat: reports current usage counts and this install's currently-applied
/// license token to a PegasusOne-hosted check-in service, purely for the business's own visibility (renewal
/// tracking, usage dashboards) — never a precondition for anything the product does locally.
///
/// This is the single most important behavioral contract in this file: an unreachable, misconfigured, or
/// entirely absent endpoint must NEVER throw out of <see cref="ExecuteAsync"/>, never block startup or any
/// other Worker hosted service, and never escalate past a Warning-level log line — a customer with zero
/// route to PegasusOne (on-prem, air-gapped) is the expected steady state for some installs, not an
/// incident. <see cref="LicenseUsageSnapshotWorker"/> is the actual local, offline-safe tamper-evident
/// record; this worker is strictly a best-effort courier on top of it.
/// </summary>
public sealed class LicenseHeartbeatWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IOptions<LicenseHeartbeatOptions> _options;
    private readonly ISystemSettingsCache _settingsCache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LicenseHeartbeatWorker> _logger;

    public LicenseHeartbeatWorker(
        IServiceScopeFactory serviceScopeFactory,
        IOptions<LicenseHeartbeatOptions> options,
        ISystemSettingsCache settingsCache,
        IHttpClientFactory httpClientFactory,
        ILogger<LicenseHeartbeatWorker> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _options = options;
        _settingsCache = settingsCache;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalMinutes = _options.Value.IntervalMinutes;
            try
            {
                var enabled = await _settingsCache.GetBoolAsync(
                    "LicenseHeartbeat:Enabled", _options.Value.Enabled, stoppingToken);
                intervalMinutes = await _settingsCache.GetIntAsync(
                    "LicenseHeartbeat:IntervalMinutes", _options.Value.IntervalMinutes, stoppingToken);

                if (!enabled)
                {
                    _logger.LogDebug(
                        "License heartbeat worker is disabled. Set LicenseHeartbeat:Enabled=true to run it.");
                }
                else
                {
                    var endpointUrl = await _settingsCache.GetStringAsync(
                        "LicenseHeartbeat:EndpointUrl", _options.Value.EndpointUrl ?? string.Empty, stoppingToken);

                    if (string.IsNullOrWhiteSpace(endpointUrl))
                    {
                        // The expected default: no real endpoint exists at a known address yet. Deliberately
                        // Debug, not Warning/Error — this is normal configuration, not a failure to report on.
                        _logger.LogDebug(
                            "License heartbeat endpoint is not configured (LicenseHeartbeat:EndpointUrl is " +
                            "empty) — skipping this tick without attempting any network call.");
                    }
                    else
                    {
                        await SendHeartbeatAsync(endpointUrl, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                // Belt-and-braces: SendHeartbeatAsync already catches every network/serialization failure
                // itself and only ever logs a Warning. Anything reaching here is unexpected, but this worker
                // must still never crash or take any other hosted service down with it.
                _logger.LogWarning(exception, "License heartbeat tick failed unexpectedly; will retry next interval.");
            }

            await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, intervalMinutes)), stoppingToken);
        }
    }

    private async Task SendHeartbeatAsync(string endpointUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var licenseService = scope.ServiceProvider.GetRequiredService<ILicenseService>();
            var appSecretAccessor = scope.ServiceProvider.GetRequiredService<IAppSecretAccessor>();
            var usageCountsProvider = scope.ServiceProvider.GetRequiredService<ILicenseUsageCountsProvider>();
            var executionStatsProvider =
                scope.ServiceProvider.GetRequiredService<ILicenseUsageExecutionStatsProvider>();

            var usageCounts = await usageCountsProvider.GetCurrentCountsAsync(cancellationToken);
            var executionStats = await executionStatsProvider.GetCurrentStatsAsync(cancellationToken);

            var request = new LicenseCheckinRequest
            {
                InstallationId = appSecretAccessor.InstallationId,
                LicenseToken = licenseService.CurrentRawToken,
                ObservedUtc = DateTime.UtcNow,
                Counts = new LicenseCheckinCounts
                {
                    UserCount = usageCounts.UserCount,
                    SourceConnectionCount = usageCounts.SourceConnectionCount,
                    TenantCount = usageCounts.TenantCount,
                    WorkflowCount = usageCounts.WorkflowCount,
                    CumulativeConfiguredPipelineRunCount = executionStats.CumulativeConfiguredPipelineRunCount,
                    CumulativeRuntimeWorkflowRunCount = executionStats.CumulativeRuntimeWorkflowRunCount,
                    ProcessedRecordsThisMonth = executionStats.ProcessedRecordsThisMonth,
                },
            };

            var httpClient = _httpClientFactory.CreateClient(nameof(LicenseHeartbeatWorker));
            httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.Value.TimeoutSeconds));

            var requestUri = BuildCheckinUri(endpointUrl);
            using var response = await httpClient.PostAsJsonAsync(requestUri, request, JsonOptions, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("License heartbeat check-in succeeded ({StatusCode}).", (int)response.StatusCode);
            }
            else
            {
                // A reachable server that said no is treated exactly like an unreachable one: Warning only,
                // never louder, and the next tick simply tries again with fresh counts.
                _logger.LogWarning(
                    "License heartbeat check-in was rejected by the server ({StatusCode}). Will retry next interval.",
                    (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // The expected steady state for an offline/air-gapped install: DNS failure, connection refused,
            // TLS error, timeout, etc. This is normal, not an incident — Warning only. The next tick simply
            // tries again; nothing here needs to remember or retry within this tick.
            _logger.LogWarning(
                exception, "License heartbeat check-in failed (server unreachable or request failed). Will retry next interval.");
        }
    }

    /// <summary>Appends the well-known check-in route to a configured base URL, unless the configured value
    /// already ends with it (so either shape — a bare base URL or the full check-in URL — works).</summary>
    private static string BuildCheckinUri(string endpointUrl)
    {
        var trimmed = endpointUrl.TrimEnd('/');
        return trimmed.EndsWith("/api/checkin", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + "/api/checkin";
    }
}
