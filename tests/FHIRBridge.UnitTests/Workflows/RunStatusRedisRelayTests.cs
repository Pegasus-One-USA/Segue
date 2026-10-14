using System.Net;
using FHIRBridge.Infrastructure.Workflows;
using FHIRBridge.Runtime.Application.Workflows;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace FHIRBridge.UnitTests.Workflows;

// A run the Worker finished (resumed after a bulk $export, or scheduled) stayed "Running" on Execution History until a
// manual refresh: the Worker had no IRunStatusNotifier, so no status change ever reached the API's SignalR hub. These
// cover the Redis path that now carries it: RedisRunStatusPublisher (Worker) -> RunStatusChannel -> RedisRunStatusRelay
// (API) -> the API's own notifier.
public sealed class RunStatusRedisRelayTests
{
    private static readonly RunStatusChangedEvent SucceededEvent = new(
        Guid.Parse("96a8fc59-2e10-4d96-925e-a3bb9840482c"),
        Guid.Parse("5a762949-ac9b-45c3-a896-8f1b2700cc7c"),
        "Succeeded",
        new DateTimeOffset(2026, 10, 13, 9, 19, 47, TimeSpan.Zero),
        ErrorMessage: null,
        ErrorReferenceId: null);

    [Fact]
    public async Task Publisher_publishes_the_event_as_json_on_the_run_status_channel()
    {
        var redis = new FakeRedis();
        var publisher = new RedisRunStatusPublisher(redis.Multiplexer.Object);
        var failed = SucceededEvent with { Status = "Failed", ErrorMessage = "Bulk export failed.", ErrorReferenceId = "ERR-20261013-000001" };

        await publisher.NotifyAsync(failed, CancellationToken.None);

        redis.Published.Should().ContainSingle();
        redis.Published[0].Channel.Should().Be(RunStatusChannel.Name);
        redis.Published[0].Channel.ToString().Should().Be("fhirbridge:run-status-changed");
        RunStatusChannel.TryDeserialize(redis.Published[0].Message, out var roundTripped, out _).Should().BeTrue();
        roundTripped.Should().Be(failed);
    }

    [Fact]
    public async Task Relay_forwards_an_event_the_Worker_published_to_the_API_notifier()
    {
        var redis = new FakeRedis();
        var hub = new RecordingNotifier();
        var relay = new RedisRunStatusRelay(redis.Multiplexer.Object, hub, NullLogger<RedisRunStatusRelay>.Instance);
        await relay.StartAsync(CancellationToken.None);

        await new RedisRunStatusPublisher(redis.Multiplexer.Object).NotifyAsync(SucceededEvent, CancellationToken.None);

        hub.Received.Should().ContainSingle().Which.Should().Be(SucceededEvent);
    }

    [Fact]
    public async Task Relay_forwards_a_duplicate_event_unchanged_and_keeps_no_state_between_them()
    {
        // With N API replicas each one relays the same message, so a browser can see it N times. The relay must not
        // alter, reorder or drop them: each copy reaches the hub exactly as published.
        var redis = new FakeRedis();
        var hub = new RecordingNotifier();
        var relay = new RedisRunStatusRelay(redis.Multiplexer.Object, hub, NullLogger<RedisRunStatusRelay>.Instance);
        await relay.StartAsync(CancellationToken.None);
        var publisher = new RedisRunStatusPublisher(redis.Multiplexer.Object);

        await publisher.NotifyAsync(SucceededEvent, CancellationToken.None);
        await publisher.NotifyAsync(SucceededEvent, CancellationToken.None);

        hub.Received.Should().HaveCount(2).And.OnlyContain(e => e == SucceededEvent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"workflowRunId\":\"00000000-0000-0000-0000-000000000000\",\"workflowDefinitionId\":\"5a762949-ac9b-45c3-a896-8f1b2700cc7c\",\"status\":\"Succeeded\",\"occurredAt\":\"2026-10-13T09:19:47+00:00\"}")]
    [InlineData("{\"workflowRunId\":\"96a8fc59-2e10-4d96-925e-a3bb9840482c\",\"workflowDefinitionId\":\"5a762949-ac9b-45c3-a896-8f1b2700cc7c\",\"status\":\"Done\",\"occurredAt\":\"2026-10-13T09:19:47+00:00\"}")]
    [InlineData("{\"workflowRunId\":\"96a8fc59-2e10-4d96-925e-a3bb9840482c\",\"workflowDefinitionId\":\"5a762949-ac9b-45c3-a896-8f1b2700cc7c\",\"status\":\"2\",\"occurredAt\":\"2026-10-13T09:19:47+00:00\"}")]
    [InlineData("{\"workflowRunId\":\"96a8fc59-2e10-4d96-925e-a3bb9840482c\",\"workflowDefinitionId\":\"5a762949-ac9b-45c3-a896-8f1b2700cc7c\",\"status\":\"succeeded\",\"occurredAt\":\"2026-10-13T09:19:47+00:00\"}")]
    public async Task Relay_drops_a_message_that_is_not_a_complete_status_event(string payload)
    {
        var redis = new FakeRedis();
        var hub = new RecordingNotifier();
        var relay = new RedisRunStatusRelay(redis.Multiplexer.Object, hub, NullLogger<RedisRunStatusRelay>.Instance);
        await relay.StartAsync(CancellationToken.None);

        var deliver = () => redis.Deliver(RunStatusChannel.Name, payload);

        deliver.Should().NotThrow();
        hub.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task Relay_keeps_forwarding_after_the_notifier_fails_once()
    {
        var redis = new FakeRedis();
        var hub = new RecordingNotifier { FailNext = 1 };
        var relay = new RedisRunStatusRelay(redis.Multiplexer.Object, hub, NullLogger<RedisRunStatusRelay>.Instance);
        await relay.StartAsync(CancellationToken.None);
        var publisher = new RedisRunStatusPublisher(redis.Multiplexer.Object);

        var first = () => publisher.NotifyAsync(SucceededEvent, CancellationToken.None);
        await first.Should().NotThrowAsync();
        await publisher.NotifyAsync(SucceededEvent with { Status = "Failed" }, CancellationToken.None);

        hub.Received.Should().ContainSingle().Which.Status.Should().Be("Failed");
    }

    [Fact]
    public async Task Relay_subscribes_once_Redis_comes_back_when_it_was_down_at_start_up()
    {
        var redis = new FakeRedis { FailNextSubscribes = 1 };
        var hub = new RecordingNotifier();
        var relay = new RedisRunStatusRelay(redis.Multiplexer.Object, hub, NullLogger<RedisRunStatusRelay>.Instance);

        var start = () => relay.StartAsync(CancellationToken.None);
        await start.Should().NotThrowAsync();
        redis.ActiveSubscriptions.Should().Be(0);

        redis.RestoreConnection();
        await new RedisRunStatusPublisher(redis.Multiplexer.Object).NotifyAsync(SucceededEvent, CancellationToken.None);

        redis.ActiveSubscriptions.Should().Be(1);
        hub.Received.Should().ContainSingle().Which.Should().Be(SucceededEvent);
    }

    [Fact]
    public async Task Relay_does_not_subscribe_a_second_time_on_reconnect_once_subscribed()
    {
        // StackExchange.Redis restores an existing subscription itself after a reconnect; subscribing again would make
        // every event arrive twice from this one replica.
        var redis = new FakeRedis();
        var relay = new RedisRunStatusRelay(redis.Multiplexer.Object, new RecordingNotifier(), NullLogger<RedisRunStatusRelay>.Instance);
        await relay.StartAsync(CancellationToken.None);

        redis.RestoreConnection();
        redis.RestoreConnection();

        redis.SubscribeCalls.Should().Be(1);
        redis.ActiveSubscriptions.Should().Be(1);
    }

    [Fact]
    public async Task Relay_stops_forwarding_and_ignores_reconnects_after_stop()
    {
        var redis = new FakeRedis();
        var hub = new RecordingNotifier();
        var relay = new RedisRunStatusRelay(redis.Multiplexer.Object, hub, NullLogger<RedisRunStatusRelay>.Instance);
        await relay.StartAsync(CancellationToken.None);

        await relay.StopAsync(CancellationToken.None);
        redis.RestoreConnection();
        await new RedisRunStatusPublisher(redis.Multiplexer.Object).NotifyAsync(SucceededEvent, CancellationToken.None);

        redis.ActiveSubscriptions.Should().Be(0);
        redis.SubscribeCalls.Should().Be(1);
        hub.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task Publisher_lets_a_Redis_failure_surface_to_the_orchestrator_which_swallows_it()
    {
        // RankedWorkflowOrchestrator.NotifyRunStatusAsync catches whatever a notifier throws; the publisher must not
        // hide it (it would never be reported) and must not block on it.
        var redis = new FakeRedis { PublishFails = true };
        var publisher = new RedisRunStatusPublisher(redis.Multiplexer.Object);

        var publish = () => publisher.NotifyAsync(SucceededEvent, CancellationToken.None);

        await publish.Should().ThrowAsync<RedisConnectionException>();
    }

    [Fact]
    public async Task Relay_forwards_messages_one_at_a_time_in_the_order_they_arrived()
    {
        // PR #247 review: each message was forwarded on its own fire-and-forget task, so a slow "Running" push could
        // land after the "Succeeded" that followed it, and the Workflows list (which patches the row from the event)
        // would end on Running. The second forward must not start before the first has finished.
        var redis = new FakeRedis();
        var hub = new GatedNotifier();
        var relay = new RedisRunStatusRelay(redis.Multiplexer.Object, hub, NullLogger<RedisRunStatusRelay>.Instance);
        await relay.StartAsync(CancellationToken.None);
        var running = SucceededEvent with { Status = "Running" };

        redis.Deliver(RunStatusChannel.Name, RunStatusChannel.Serialize(running));
        redis.Deliver(RunStatusChannel.Name, RunStatusChannel.Serialize(SucceededEvent));

        hub.Started.Should().Equal("Running");     // "Succeeded" waits while "Running" is still being pushed
        hub.Release();
        await hub.Drained;

        hub.Started.Should().Equal("Running", "Succeeded");
        hub.Completed.Should().Equal("Running", "Succeeded");
    }

    [Fact]
    public async Task A_hung_push_times_out_and_later_events_still_go_out()
    {
        // PR #247 review: pushes go out one at a time, so a push that never finished held up every later event and
        // the portal stopped updating live. Each push is now bounded, even for a notifier that ignores its token.
        var redis = new FakeRedis();
        var hub = new GatedNotifier { IgnoreCancellation = true };
        var relay = new RedisRunStatusRelay(redis.Multiplexer.Object, hub, NullLogger<RedisRunStatusRelay>.Instance, TimeSpan.FromMilliseconds(100));
        await relay.StartAsync(CancellationToken.None);

        redis.Deliver(RunStatusChannel.Name, RunStatusChannel.Serialize(SucceededEvent with { Status = "Running" }));
        redis.Deliver(RunStatusChannel.Name, RunStatusChannel.Serialize(SucceededEvent));

        await hub.SecondStarted;
        hub.Started.Should().Equal("Running", "Succeeded");
        hub.Release();
    }

    [Fact]
    public async Task Stopping_abandons_the_push_in_flight_and_drops_queued_events()
    {
        var redis = new FakeRedis();
        var hub = new GatedNotifier();
        var relay = new RedisRunStatusRelay(redis.Multiplexer.Object, hub, NullLogger<RedisRunStatusRelay>.Instance);
        await relay.StartAsync(CancellationToken.None);

        redis.Deliver(RunStatusChannel.Name, RunStatusChannel.Serialize(SucceededEvent with { Status = "Running" }));
        redis.Deliver(RunStatusChannel.Name, RunStatusChannel.Serialize(SucceededEvent));
        await relay.StopAsync(CancellationToken.None);
        await hub.FirstCancelled;
        await Task.Delay(100);

        hub.Started.Should().Equal("Running"); // "Succeeded" was queued behind it and is never sent
    }

    /// <summary>Holds the first push open until <see cref="Release"/>; records the order pushes start and finish in.</summary>
    private sealed class GatedNotifier : IRunStatusNotifier
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Started { get; } = [];

        public List<string> Completed { get; } = [];

        /// <summary>The first push waits on the gate alone, as a notifier that never honours its token would.</summary>
        public bool IgnoreCancellation { get; init; }

        public Task Drained => _drained.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public Task SecondStarted => _secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public Task FirstCancelled => _firstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Release() => _gate.TrySetResult();

        public async Task NotifyAsync(RunStatusChangedEvent statusEvent, CancellationToken cancellationToken)
        {
            lock (Started) Started.Add(statusEvent.Status);
            if (Started.Count == 2)
            {
                _secondStarted.TrySetResult();
            }

            if (Started.Count == 1)
            {
                if (IgnoreCancellation)
                {
                    await _gate.Task;
                }
                else
                {
                    cancellationToken.Register(() => _firstCancelled.TrySetResult());
                    await _gate.Task.WaitAsync(cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
                }
            }

            lock (Completed) Completed.Add(statusEvent.Status);
            if (Completed.Count == 2)
            {
                _drained.TrySetResult();
            }
        }
    }

    private sealed class RecordingNotifier : IRunStatusNotifier
    {
        public List<RunStatusChangedEvent> Received { get; } = [];

        public int FailNext { get; set; }

        public Task NotifyAsync(RunStatusChangedEvent statusEvent, CancellationToken cancellationToken)
        {
            if (FailNext > 0)
            {
                FailNext--;
                throw new InvalidOperationException("Hub unavailable.");
            }

            Received.Add(statusEvent);
            return Task.CompletedTask;
        }
    }

    /// <summary>An in-process stand-in for one Redis server: publishes reach every live subscription on the channel.</summary>
    private sealed class FakeRedis
    {
        private readonly List<(RedisChannel Channel, Action<RedisChannel, RedisValue> Handler)> _subscriptions = [];

        public FakeRedis()
        {
            Multiplexer.Setup(m => m.GetSubscriber(It.IsAny<object?>())).Returns(Subscriber.Object);

            Subscriber
                .Setup(s => s.SubscribeAsync(It.IsAny<RedisChannel>(), It.IsAny<Action<RedisChannel, RedisValue>>(), It.IsAny<CommandFlags>()))
                .Returns((RedisChannel channel, Action<RedisChannel, RedisValue> handler, CommandFlags _) =>
                {
                    SubscribeCalls++;
                    if (FailNextSubscribes > 0)
                    {
                        FailNextSubscribes--;
                        return Task.FromException(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis is down."));
                    }

                    _subscriptions.Add((channel, handler));
                    return Task.CompletedTask;
                });

            Subscriber
                .Setup(s => s.UnsubscribeAsync(It.IsAny<RedisChannel>(), It.IsAny<Action<RedisChannel, RedisValue>?>(), It.IsAny<CommandFlags>()))
                .Returns((RedisChannel channel, Action<RedisChannel, RedisValue>? handler, CommandFlags _) =>
                {
                    _subscriptions.RemoveAll(s => s.Channel == channel && (handler is null || s.Handler == handler));
                    return Task.CompletedTask;
                });

            Subscriber
                .Setup(s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns((RedisChannel channel, RedisValue message, CommandFlags _) =>
                {
                    if (PublishFails)
                    {
                        return Task.FromException<long>(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis is down."));
                    }

                    Published.Add((channel, message));
                    return Task.FromResult((long)Deliver(channel, message));
                });
        }

        public Mock<IConnectionMultiplexer> Multiplexer { get; } = new();

        public Mock<ISubscriber> Subscriber { get; } = new();

        public int FailNextSubscribes { get; set; }

        public bool PublishFails { get; set; }

        public int SubscribeCalls { get; private set; }

        public int ActiveSubscriptions => _subscriptions.Count;

        public List<(RedisChannel Channel, RedisValue Message)> Published { get; } = [];

        public int Deliver(RedisChannel channel, RedisValue message)
        {
            var receivers = _subscriptions.Where(s => s.Channel == channel).ToList();
            foreach (var receiver in receivers)
            {
                receiver.Handler(channel, message);
            }

            return receivers.Count;
        }

        public void RestoreConnection()
            => Multiplexer.Raise(
                m => m.ConnectionRestored += null,
                new ConnectionFailedEventArgs(
                    Multiplexer.Object,
                    new DnsEndPoint("localhost", 6379),
                    ConnectionType.Subscription,
                    ConnectionFailureType.None,
                    null!,
                    "test"));
    }
}
