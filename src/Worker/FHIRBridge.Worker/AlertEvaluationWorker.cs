using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Governance;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Worker;

/// <summary>Periodically evaluates every enabled AlertRule and fires real alerts — see IAlertEvaluationService.</summary>
public sealed class AlertEvaluationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IOptions<AlertEvaluationOptions> _options;
    private readonly ISystemSettingsCache _settingsCache;
    private readonly ILogger<AlertEvaluationWorker> _logger;

    public AlertEvaluationWorker(
        IServiceScopeFactory serviceScopeFactory,
        IOptions<AlertEvaluationOptions> options,
        ISystemSettingsCache settingsCache,
        ILogger<AlertEvaluationWorker> logger)
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
                "AlertEvaluation:Enabled", _options.Value.Enabled, stoppingToken);
            if (!enabled)
            {
                _logger.LogInformation("Alert evaluation worker is disabled. Set AlertEvaluation:Enabled=true to evaluate alert rules.");
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                continue;
            }

            await EvaluateAsync(stoppingToken);

            var intervalSeconds = await _settingsCache.GetIntAsync(
                "AlertEvaluation:IntervalSeconds", _options.Value.IntervalSeconds, stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(60, intervalSeconds)), stoppingToken);
        }
    }

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var evaluationService = scope.ServiceProvider.GetRequiredService<IAlertEvaluationService>();
            var firedCount = await evaluationService.EvaluateAsync(DateTime.UtcNow, cancellationToken);

            if (firedCount > 0)
            {
                _logger.LogWarning("Alert evaluation fired {FiredCount} alert(s).", firedCount);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Alert evaluation run failed.");
        }
    }
}
