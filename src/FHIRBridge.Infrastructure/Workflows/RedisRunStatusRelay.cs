using FHIRBridge.Runtime.Application.Workflows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace FHIRBridge.Infrastructure.Workflows;

/// <summary>
/// The API's half of <see cref="RunStatusChannel"/>: forwards every run status change the Worker publishes to this
/// host's <see cref="IRunStatusNotifier"/> (the SignalR hub), so Execution History, the Workflows list and the
/// Dashboard update for Worker runs exactly as they already do for the API's own.
/// <para>Registered in the API only when <c>ConnectionStrings:Redis</c> is set. Every API replica subscribes and
/// forwards, so with N replicas a browser can get the same event up to N times; that is harmless, because every
/// screen either re-fetches (debounced) or overwrites the row with the same values.</para>
/// <para>Subscribing follows <c>LicenseService</c>: never on the start-up path's critical line, retried on
/// <see cref="IConnectionMultiplexer.ConnectionRestored"/> when the first attempt failed. Once a subscription is
/// made, StackExchange.Redis itself re-establishes it after a reconnect.</para>
/// </summary>
public sealed class RedisRunStatusRelay : IHostedService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IRunStatusNotifier _notifier;
    private readonly ILogger<RedisRunStatusRelay> _logger;
    private readonly EventHandler<ConnectionFailedEventArgs> _onConnectionRestored;
    private readonly Action<RedisChannel, RedisValue> _onMessage;
    private readonly object _forwardGate = new();
    /// <summary>The last forward queued. Each message chains onto it, so pushes go out one at a time in arrival order.</summary>
    private Task _forwardTail = Task.CompletedTask;
    private int _subscribeState; // 0 = not subscribed, 1 = subscribing, 2 = subscribed
    private volatile bool _stopped;
    /// <summary>Cancelled on stop: a push in flight is abandoned and anything still queued is dropped.</summary>
    private readonly CancellationTokenSource _stopping = new();
    private readonly TimeSpan _pushTimeout;

    /// <summary>The longest one push may take. Pushes go out one at a time, so without a bound a single hung push held
    /// up every status change after it and the portal stopped updating live.</summary>
    public static readonly TimeSpan DefaultPushTimeout = TimeSpan.FromSeconds(10);

    public RedisRunStatusRelay(
        IConnectionMultiplexer redis,
        IRunStatusNotifier notifier,
        ILogger<RedisRunStatusRelay> logger)
        : this(redis, notifier, logger, DefaultPushTimeout)
    {
    }

    /// <summary>Internal so DI only ever sees the public constructor; tests pass a short timeout.</summary>
    internal RedisRunStatusRelay(
        IConnectionMultiplexer redis,
        IRunStatusNotifier notifier,
        ILogger<RedisRunStatusRelay> logger,
        TimeSpan pushTimeout)
    {
        _redis = redis;
        _notifier = notifier;
        _logger = logger;
        _pushTimeout = pushTimeout;
        _onConnectionRestored = (_, _) => TrySubscribe();
        _onMessage = (channel, message) => EnqueueForward(message);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopped = false;
        _redis.ConnectionRestored += _onConnectionRestored;
        TrySubscribe();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopped = true;
        _stopping.Cancel();
        _redis.ConnectionRestored -= _onConnectionRestored;
        Interlocked.Exchange(ref _subscribeState, 0);
        try
        {
            // Fire-and-forget: a hung Redis must never hold up shutdown.
            _ = _redis.GetSubscriber().UnsubscribeAsync(RunStatusChannel.Name, _onMessage);
        }
        catch (Exception)
        {
            // Best-effort — the process is going away regardless.
        }

        return Task.CompletedTask;
    }

    /// <summary>Queues a message behind the one before it. Forwarding each on its own task let a slow "Running" push
    /// land after the "Succeeded" that followed it, and the Workflows list (which patches its row from the event) ended
    /// on Running. ForwardAsync never throws, so one failed push never stalls the queue.</summary>
    private void EnqueueForward(RedisValue message)
    {
        lock (_forwardGate)
        {
            _forwardTail = _forwardTail
                .ContinueWith(_ => ForwardAsync(message), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
                .Unwrap();
        }
    }

    /// <summary>Forwards one channel message. Never throws: a bad message, a failed push or one that overran the push
    /// timeout is logged and dropped, and the next message is handled as normal. Once the relay has stopped, queued
    /// messages are dropped unsent.</summary>
    public async Task ForwardAsync(RedisValue message)
    {
        if (_stopped)
        {
            return;
        }

        if (!RunStatusChannel.TryDeserialize(message, out var statusEvent, out var reason))
        {
            // The payload itself is not logged: a run's error message can carry source-system detail.
            _logger.LogWarning("Dropped a message on {Channel}: {Reason}.", RunStatusChannel.Name, reason);
            return;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            timeout.CancelAfter(_pushTimeout);
            // The token cancels the push on stop or timeout; WaitAsync bounds a notifier that ignores it, so it still
            // releases the queue. (WaitAsync takes no stop token: completing it on stop would dispose `timeout` before
            // the stop reached the notifier through it.)
            await _notifier.NotifyAsync(statusEvent!, timeout.Token).WaitAsync(_pushTimeout);
        }
        catch (Exception) when (_stopped)
        {
            // Shutting down: the push was abandoned on purpose.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception,
                "Could not push the {Status} status of workflow run {WorkflowRunId} to the browser.",
                statusEvent!.Status, statusEvent.WorkflowRunId);
        }
    }

    // Fire-and-forget async, not the synchronous Subscribe — see InProcessAllowedCorsOriginsCache for why.
    private void TrySubscribe()
    {
        if (_stopped || Interlocked.CompareExchange(ref _subscribeState, 1, 0) != 0)
        {
            return;
        }

        _ = SubscribeAsyncCore();

        async Task SubscribeAsyncCore()
        {
            try
            {
                await _redis.GetSubscriber().SubscribeAsync(RunStatusChannel.Name, _onMessage);
                Interlocked.CompareExchange(ref _subscribeState, 2, 1);
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref _subscribeState, 0, 1);
                // Still down — ConnectionRestored calls this again. Until then Worker runs show on the next fetch.
                _logger.LogWarning(exception,
                    "Could not subscribe to {Channel}; Worker run status changes will not update the browser live until Redis is reachable.",
                    RunStatusChannel.Name);
            }
        }
    }
}
