using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Governance;

/// <summary>The correlation id of the request / run the current code is part of (falls back to nothing). Read on the
/// calling thread when an error is queued, because the background writer has no request to read it from.</summary>
public interface ICorrelationIdAccessor
{
    string? CorrelationId { get; }
}

/// <summary>
/// Hands a finished (already scrubbed) error entry to the background writer. Recording an error must never make the
/// code that hit the error wait - not for a database that is slow, locked by maintenance, or briefly unreachable.
/// </summary>
public interface IErrorWriteQueue
{
    /// <summary>Queues the entry and returns at once. False only when the queue is full (the entry is then dropped and counted).</summary>
    bool TryEnqueue(ErrorEntry entry);
}

public sealed class ErrorWriteQueue : IErrorWriteQueue
{
    // Large enough to ride out a maintenance lock of a minute or more at a steady error rate, small enough to bound memory.
    public const int Capacity = 20_000;

    private readonly Channel<ErrorEntry> _channel = Channel.CreateBounded<ErrorEntry>(
        new BoundedChannelOptions(Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    private long _dropped;

    internal ChannelReader<ErrorEntry> Reader => _channel.Reader;

    internal void Complete() => _channel.Writer.TryComplete();

    internal long TakeDropped() => Interlocked.Exchange(ref _dropped, 0);

    public bool TryEnqueue(ErrorEntry entry)
    {
        if (_channel.Writer.TryWrite(entry))
        {
            return true;
        }

        Interlocked.Increment(ref _dropped);
        return false;
    }
}

/// <summary>
/// Writes queued errors to the configured sinks, off the request path. A write that fails (table locked, database
/// down) is retried with a growing pause on the SAME reference id, so the id already shown to a user still resolves;
/// if it keeps failing the entry goes to Application Insights when that is configured, otherwise it is dropped with a
/// warning in the application log (reference id only - never the error text).
/// </summary>
public sealed class ErrorWriteService : IHostedService
{
    private static readonly TimeSpan[] RetryPauses =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(20);

    private readonly ErrorWriteQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ErrorWriteService> _logger;
    private CancellationTokenSource? _stopping;
    private Task? _worker;
    private volatile bool _draining;

    public ErrorWriteService(IErrorWriteQueue queue, IServiceScopeFactory scopeFactory, ILogger<ErrorWriteService> logger)
    {
        _queue = (ErrorWriteQueue)queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        _worker = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Let what is already queued finish (bounded), without the retry pauses, then stop.
        _draining = true;
        _queue.Complete();
        try
        {
            if (_worker is not null)
            {
                await _worker.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            }
        }
        catch
        {
            // shutting down; whatever is still queued after the grace period is lost
        }

        _stopping?.Cancel();
    }

    private async Task RunAsync(CancellationToken stopping)
    {
        try
        {
            await foreach (var entry in _queue.Reader.ReadAllAsync(stopping))
            {
                await WriteWithRetryAsync(entry, stopping);
                ReportDropped();
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private void ReportDropped()
    {
        var dropped = _queue.TakeDropped();
        if (dropped > 0)
        {
            _logger.LogWarning(
                "{Dropped} error entr{Suffix} could not be queued because the error writer was {Capacity} entries behind; they are not in the error log.",
                dropped, dropped == 1 ? "y" : "ies", ErrorWriteQueue.Capacity);
        }
    }

    private async Task WriteWithRetryAsync(ErrorEntry entry, CancellationToken stopping)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                timeout.CancelAfter(WriteTimeout);

                // A fresh scope per write: a failed save must not poison the next entry's DbContext.
                using var scope = _scopeFactory.CreateScope();
                var router = scope.ServiceProvider.GetRequiredService<IErrorSinkRouter>();
                await router.WriteAsync(entry, timeout.Token);
                return;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception caught)
            {
                // A duplicate reference id will fail identically every time, so waiting ~100 s on it would only stall
                // every entry queued behind it.
                if (attempt >= RetryPauses.Length || _draining || IsDuplicateKey(caught))
                {
                    await GiveUpAsync(entry, caught);
                    return;
                }

                try
                {
                    await Task.Delay(RetryPauses[attempt], stopping);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>True for a unique-key violation (PostgreSQL SqlState 23505, SQL Server error 2601 / 2627). Governance has no
    /// database-provider reference, so the provider exception is recognised by its public SqlState / Number property.</summary>
    internal static bool IsDuplicateKey(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var type = current.GetType();
            if (type.GetProperty("SqlState")?.GetValue(current) is string state && state == "23505")
            {
                return true;
            }

            if (type.GetProperty("Number")?.GetValue(current) is int number && number is 2601 or 2627)
            {
                return true;
            }
        }

        return false;
    }

    private async Task GiveUpAsync(ErrorEntry entry, Exception caught)
    {
        var reachedFallback = false;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            reachedFallback = await scope.ServiceProvider.GetRequiredService<IErrorSinkRouter>().WriteFallbackAsync(entry);
        }
        catch
        {
            // best effort
        }

        // Reference id and exception type only: the entry text is deliberately not logged here.
        _logger.LogWarning(
            "Error {ErrorReferenceId} could not be written to the error log ({FailureType}){Fallback}.",
            entry.ErrorReferenceId, caught.GetType().Name, reachedFallback ? "; it was sent to Application Insights instead" : string.Empty);
    }
}
