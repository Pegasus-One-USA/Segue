using System.Collections.Concurrent;

namespace FHIRBridge.Runtime.Application.Workflows;

/// <summary>
/// The workflow runs this process is executing right now, whatever started them (the Run button, a schedule, a
/// resumed bulk export). The orchestrator holds a run here for exactly as long as it executes it; the lease heartbeat
/// renews the lease of every run held here, which is how other instances tell a live run from a dead one.
/// </summary>
public interface IActiveWorkflowRuns
{
    /// <summary>This process: host name, process id and an id unique to this start.</summary>
    string InstanceId { get; }

    /// <summary>Holds <paramref name="workflowRunId"/> until the returned handle is disposed.</summary>
    IDisposable Hold(Guid workflowRunId);

    IReadOnlyCollection<Guid> Snapshot();
}

public sealed class ActiveWorkflowRuns : IActiveWorkflowRuns
{
    private readonly ConcurrentDictionary<Guid, int> _held = new();

    public string InstanceId { get; } =
        $"{Environment.MachineName}/{Environment.ProcessId}/{Guid.NewGuid().ToString("N")[..8]}";

    public IDisposable Hold(Guid workflowRunId)
    {
        _held.AddOrUpdate(workflowRunId, 1, (_, count) => count + 1);
        return new Release(this, workflowRunId);
    }

    public IReadOnlyCollection<Guid> Snapshot() => _held.Keys.ToArray();

    private void Drop(Guid workflowRunId)
    {
        while (_held.TryGetValue(workflowRunId, out var count))
        {
            if (count <= 1
                ? _held.TryRemove(new KeyValuePair<Guid, int>(workflowRunId, count))
                : _held.TryUpdate(workflowRunId, count - 1, count))
            {
                return;
            }
        }
    }

    private sealed class Release : IDisposable
    {
        private readonly ActiveWorkflowRuns _owner;
        private readonly Guid _workflowRunId;
        private int _released;

        public Release(ActiveWorkflowRuns owner, Guid workflowRunId)
        {
            _owner = owner;
            _workflowRunId = workflowRunId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _owner.Drop(_workflowRunId);
            }
        }
    }
}

/// <summary>
/// Lease timings. A process renews its runs' leases every <see cref="HeartbeatInterval"/> for
/// <see cref="Duration"/>, so a live run's lease never lapses unless its process misses three heartbeats in a row
/// (stopped, crashed, or hung). A run from before leases existed counts as dead once it is older than
/// <see cref="LegacyGrace"/>.
/// </summary>
public static class WorkflowRunLease
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan LegacyGrace = TimeSpan.FromMinutes(30);

    /// <summary>The error Execution History shows on a run whose process stopped before it finished.</summary>
    public const string InterruptedReason =
        "Interrupted: the process running this run stopped (restart, shutdown or crash) or stopped responding before " +
        "the run finished, so it did not complete. Nothing after the last completed step was done. Run it again.";
}
