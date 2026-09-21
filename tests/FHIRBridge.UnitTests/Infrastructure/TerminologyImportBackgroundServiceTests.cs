using FHIRBridge.Infrastructure.Terminology;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.UnitTests.Infrastructure;

/// <summary>
/// Covers the drain loop's concurrency: imports used to run strictly one after another, which made the
/// Terminology Server screen's "scan on load, sync whatever is out of date" pass a very long serial wait in
/// the monthly-release case — most of that time spent waiting on downloads rather than on the database.
/// </summary>
public sealed class TerminologyImportBackgroundServiceTests
{
    private static TerminologyImportBackgroundService CreateService(
        TerminologyImportChannel channel, int? maxConcurrency)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(maxConcurrency is null
                ? []
                : new Dictionary<string, string?> { ["Terminology:MaxConcurrentImports"] = maxConcurrency.Value.ToString() })
            .Build();

        return new TerminologyImportBackgroundService(
            channel,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TerminologyImportBackgroundService>.Instance,
            configuration);
    }

    [Fact]
    public async Task Jobs_run_concurrently_up_to_the_configured_cap()
    {
        var channel = new TerminologyImportChannel();
        var service = CreateService(channel, maxConcurrency: 3);

        var inFlight = 0;
        var peakInFlight = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        var padlock = new object();

        for (var i = 0; i < 6; i++)
        {
            channel.Enqueue(async (_, _) =>
            {
                lock (padlock)
                {
                    inFlight++;
                    peakInFlight = Math.Max(peakInFlight, inFlight);
                    // Three concurrent jobs is the cap — once that many are parked on the gate, the loop
                    // cannot start a fourth, which is precisely what this asserts.
                    if (++startedCount == 3) allStarted.TrySetResult();
                }

                await gate.Task;

                lock (padlock)
                {
                    inFlight--;
                }
            });
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = service.StartAsync(cts.Token);

        // Three jobs reach the gate and hold there; a serial drain loop could only ever have started one.
        await allStarted.Task.WaitAsync(cts.Token);
        peakInFlight.Should().Be(3);

        gate.SetResult();
        await run;
        await service.StopAsync(CancellationToken.None);

        peakInFlight.Should().Be(3, "the semaphore must cap concurrency, not merely enable it");
    }

    [Fact]
    public async Task A_failing_job_does_not_stop_the_ones_behind_it()
    {
        var channel = new TerminologyImportChannel();
        var service = CreateService(channel, maxConcurrency: 1);

        var completed = 0;
        var secondRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        channel.Enqueue((_, _) => throw new InvalidOperationException("import blew up"));
        channel.Enqueue((_, _) =>
        {
            Interlocked.Increment(ref completed);
            secondRan.TrySetResult();
            return Task.CompletedTask;
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cts.Token);

        await secondRan.Task.WaitAsync(cts.Token);
        completed.Should().Be(1, "one job throwing must never take the drain loop down with it");

        await service.StopAsync(CancellationToken.None);
    }
}
