using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>Drains <see cref="TerminologyImportChannel"/>, each job in its own DI scope (a fresh
/// <c>FHIRBridgeDbContext</c> per job, since the enqueuing HTTP request's scope is long gone by the time this
/// runs). Individual import services record their own failures into their *ImportHistory table; the catch
/// here is only a last-resort net for failures before that point (e.g. DI resolution itself).
///
/// Jobs run CONCURRENTLY up to <see cref="DefaultMaxConcurrency"/>. Previously this awaited each job inside
/// the read loop, so the 13 HAPI code systems imported strictly one after another — fine for the occasional
/// manual import this was built for, but the Terminology Server screen now scans every system on load and
/// starts one sync per out-of-date system, which in the monthly-release case means most of them at once.
/// Serially that is a very long wall-clock wait for work that is mostly network-bound download time.
///
/// The cap is deliberate rather than unbounded: these imports are large and write-heavy against one
/// database, and each still holds its own DbContext and connection for its duration. Concurrency is safe
/// because every job already gets its own scope — nothing is shared between them — but the number of them
/// is not something to leave to chance, hence a small default and a configuration override
/// (<c>Terminology:MaxConcurrentImports</c>) for an install that wants to tune it. Setting it to 1 restores
/// exactly the old serial behaviour.</summary>
public sealed class TerminologyImportBackgroundService : BackgroundService
{
    /// <summary>Matches the bounded parallelism the Runtime plane's own orchestrator uses for concurrent
    /// resource extraction — enough to overlap download-bound work without letting a dozen bulk imports
    /// write to the same database at once.</summary>
    private const int DefaultMaxConcurrency = 4;

    private readonly TerminologyImportChannel _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TerminologyImportBackgroundService> _logger;
    private readonly int _maxConcurrency;

    public TerminologyImportBackgroundService(
        TerminologyImportChannel channel,
        IServiceScopeFactory scopeFactory,
        ILogger<TerminologyImportBackgroundService> logger,
        IConfiguration? configuration = null)
    {
        _channel = channel;
        _scopeFactory = scopeFactory;
        _logger = logger;

        var configured = configuration?.GetValue<int?>("Terminology:MaxConcurrentImports");
        _maxConcurrency = configured is > 0 ? configured.Value : DefaultMaxConcurrency;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var slots = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
        var running = new List<Task>();

        await foreach (var job in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            // Waiting for a slot BEFORE starting the task is what bounds concurrency: the read loop parks
            // here while the cap is reached, leaving the remaining jobs queued in the channel rather than
            // piling up as pending tasks.
            await slots.WaitAsync(stoppingToken);

            // Completed tasks are dropped each time round so this list tracks in-flight work only, rather
            // than growing for the lifetime of the process.
            running.RemoveAll(t => t.IsCompleted);
            running.Add(RunJobAsync(job, slots, stoppingToken));
        }

        // Shutdown: let whatever is still importing finish (or observe cancellation) instead of tearing the
        // host down mid-import and leaving a history row stuck at "Running".
        await Task.WhenAll(running);
    }

    private async Task RunJobAsync(
        Func<IServiceProvider, CancellationToken, Task> job, SemaphoreSlim slots, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            await job(scope.ServiceProvider, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // Swallowed, exactly as before: one job throwing must never take down the drain loop and stop
            // every later import. Now doubly important — this runs detached, so an unobserved exception here
            // would escape the read loop's own try/catch entirely.
            _logger.LogError(exception, "A terminology import background job failed.");
        }
        finally
        {
            slots.Release();
        }
    }
}
