using FHIRBridge.Runtime.Application.Workflows;
using StackExchange.Redis;

namespace FHIRBridge.Infrastructure.Workflows;

/// <summary>
/// The Worker's <see cref="IRunStatusNotifier"/>: publishes a run's status change on <see cref="RunStatusChannel"/>
/// for the API to push to the browser (see <see cref="RedisRunStatusRelay"/>). Without it, a run the Worker finished
/// stayed "Running" on Execution History until a manual refresh, because nothing ever told the browser.
/// <para>Registered in the Worker only when <c>ConnectionStrings:Redis</c> is set. The orchestrator calls it after the
/// run's status is saved, and swallows anything it throws, so a Redis outage loses only the live update: the saved
/// status is untouched and the next fetch shows it.</para>
/// </summary>
public sealed class RedisRunStatusPublisher : IRunStatusNotifier
{
    private readonly IConnectionMultiplexer _redis;

    public RedisRunStatusPublisher(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    // FireAndForget: a slow Redis must never hold up the run that is reporting its status.
    public Task NotifyAsync(RunStatusChangedEvent statusEvent, CancellationToken cancellationToken)
        => _redis.GetSubscriber().PublishAsync(
            RunStatusChannel.Name, RunStatusChannel.Serialize(statusEvent), CommandFlags.FireAndForget);
}
