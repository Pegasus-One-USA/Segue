using FHIRBridge.Application.Abstractions.Governance;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Worker;

/// <summary>Periodically evaluates every enabled AlertRule and fires real alerts — see IAlertEvaluationService.</summary>
public sealed class AlertEvaluationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IOptions<AlertEvaluationOptions> _options;
    private readonly ILogger<AlertEvaluationWorker> _logger;

    public AlertEvaluationWorker(
        IServiceScopeFactory serviceScopeFactory,
        IOptions<AlertEvaluationOptions> options,
        ILogger<AlertEvaluationWorker> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            _logger.LogInformation("Alert evaluation worker is disabled. Set AlertEvaluation:Enabled=true to evaluate alert rules.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(60, _options.Value.IntervalSeconds)));

        do
        {
            await EvaluateAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
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
