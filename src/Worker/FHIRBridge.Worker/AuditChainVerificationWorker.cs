using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Governance;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Worker;

/// <summary>
/// Periodically verifies the <c>AuditLogs</c> hash chain and raises a <c>SecurityEvent</c> if it's broken.
/// This was previously only checked on-demand inside Compliance Report generation; this worker makes it a
/// recurring, unattended check — see POL-010 §3.2.
/// </summary>
public sealed class AuditChainVerificationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IOptions<AuditChainVerificationOptions> _options;
    private readonly ISystemSettingsCache _settingsCache;
    private readonly ILogger<AuditChainVerificationWorker> _logger;

    public AuditChainVerificationWorker(
        IServiceScopeFactory serviceScopeFactory,
        IOptions<AuditChainVerificationOptions> options,
        ISystemSettingsCache settingsCache,
        ILogger<AuditChainVerificationWorker> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _options = options;
        _settingsCache = settingsCache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var enabled = await _settingsCache.GetBoolAsync(
                "AuditChainVerification:Enabled", _options.Value.Enabled, stoppingToken);
            if (!enabled)
            {
                _logger.LogInformation("Audit chain verification worker is disabled. Set AuditChainVerification:Enabled=true to run it.");
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                continue;
            }

            await VerifyAsync(stoppingToken);

            var intervalHours = await _settingsCache.GetIntAsync(
                "AuditChainVerification:IntervalHours", _options.Value.IntervalHours, stoppingToken);
            await Task.Delay(TimeSpan.FromHours(Math.Max(1, intervalHours)), stoppingToken);
        }
    }

    private async Task VerifyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var chainVerificationService = scope.ServiceProvider.GetRequiredService<IAuditChainVerificationService>();
            var result = await chainVerificationService.VerifyAsync(cancellationToken);

            if (result.IsValid)
            {
                _logger.LogInformation(
                    "Audit chain verification passed ({TotalEntries} entries checked).", result.TotalEntries);
                return;
            }

            _logger.LogCritical(
                "Audit chain verification FAILED at SequenceNumber {SequenceNumber} ({TotalEntries} entries checked).",
                result.FirstBrokenSequenceNumber, result.TotalEntries);

            var governanceLogger = scope.ServiceProvider.GetRequiredService<IGovernanceLogger>();
            await governanceLogger.LogSecurityEventAsync(
                new SecurityEventEntry(
                    "AuditChainBroken",
                    "Critical",
                    Details: $"Hash-chain verification failed at AuditLog.SequenceNumber={result.FirstBrokenSequenceNumber} " +
                             $"of {result.TotalEntries} total entries. This indicates the audit trail was tampered with " +
                             "outside the application (direct DB edit/delete) — investigate immediately."),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Audit chain verification run failed.");
        }
    }
}
