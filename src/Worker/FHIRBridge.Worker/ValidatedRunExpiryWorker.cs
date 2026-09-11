using FHIRBridge.Governance;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Worker;

/// <summary>
/// Sweeps <c>validate-run</c> attempts that were never executed into a terminal Expired state.
/// <para>Validated is deliberately non-terminal — it is the row <c>POST /run</c> continues, which is what makes a
/// single user action one Execution History entry rather than two. The cost is that an attempt which never
/// proceeds has nothing to close it: the user clicks Fetch, gets redirected to the EHR to sign in, and closes the
/// tab. Without this sweep those rows accumulate, read as though something were still pending, and stay eligible
/// for continuation long after the caller has moved on.</para>
/// <para>Modeled on <see cref="BulkExportPollWorker"/>: same isolation (a failed tick never stops the loop) and the
/// same enable/interval option shape.</para>
/// </summary>
public sealed class ValidatedRunExpiryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IOptions<ValidatedRunExpiryOptions> _options;
    private readonly ILogger<ValidatedRunExpiryWorker> _logger;

    public ValidatedRunExpiryWorker(
        IServiceScopeFactory serviceScopeFactory,
        IOptions<ValidatedRunExpiryOptions> options,
        ILogger<ValidatedRunExpiryWorker> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_options.Value.Enabled)
            {
                await SweepAsync(stoppingToken);
            }

            await Task.Delay(
                TimeSpan.FromMinutes(Math.Max(1, _options.Value.IntervalMinutes)), stoppingToken);
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();

        try
        {
            var runStore = scope.ServiceProvider.GetRequiredService<IWorkflowRunStore>();
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-Math.Max(1, _options.Value.ExpireAfterMinutes));

            var expired = await runStore.ExpireStaleValidatedAsync(cutoff, cancellationToken);
            if (expired > 0)
            {
                _logger.LogInformation(
                    "Expired {ExpiredCount} validated workflow run(s) that were never executed (older than {Cutoff:o}).",
                    expired, cutoff);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Validated-run expiry sweep failed.");

            var exceptionManager = scope.ServiceProvider.GetRequiredService<IGlobalExceptionManager>();
            await exceptionManager.CaptureAsync(
                exception,
                new ExceptionContext(Module: "Validated Run Expiry"),
                CancellationToken.None);
        }
    }
}
