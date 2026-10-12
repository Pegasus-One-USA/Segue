namespace FHIRBridge.Runtime.Application.Workflows;

/// <summary>
/// The workflow runs this process is executing right now, whatever started them (the Run button, a schedule, a
/// resumed bulk export). The orchestrator holds a run here for exactly as long as it executes it; the lease heartbeat
/// renews the lease of every run held here, which is how other instances tell a live run from a dead one.
///
/// <para><b>Fence.</b> Another instance fails a run once its lease lapses, so this process must stop working on a run
/// before then if it cannot renew. Each held run remembers until when its lease is confirmed
/// (<see cref="Confirm"/>); <see cref="HoldsLease"/> turns false <see cref="WorkflowRunLease.FenceMargin"/> before that,
/// or at once when the heartbeat finds the run closed elsewhere (<see cref="Revoke"/>). The orchestrator checks it
/// between nodes, the EHR write-back writer before every create.</para>
/// </summary>
public interface IActiveWorkflowRuns
{
    /// <summary>This process: host name, process id and an id unique to this start.</summary>
    string InstanceId { get; }

    /// <summary>Holds <paramref name="workflowRunId"/> until the returned handle is disposed.</summary>
    IDisposable Hold(Guid workflowRunId);

    IReadOnlyCollection<Guid> Snapshot();

    /// <summary>The database holds this process's lease on each held run in <paramref name="workflowRunIds"/> until
    /// <paramref name="leaseExpiresAt"/>. A run never confirmed is not fenced: no row of it shows a lease anyone could
    /// see lapse (a resumed run, or one whose Running row could not be saved).</summary>
    void Confirm(IEnumerable<Guid> workflowRunIds, DateTimeOffset leaseExpiresAt);

    /// <summary>The run was closed by another instance (its lease lapsed there, or it was cancelled as abandoned):
    /// this process must stop working on it.</summary>
    void Revoke(Guid workflowRunId);

    /// <summary>False once this process may no longer act for the run: revoked, or its confirmed lease is within
    /// <see cref="WorkflowRunLease.FenceMargin"/> of lapsing. True for a run not held here or never confirmed.</summary>
    bool HoldsLease(Guid workflowRunId, DateTimeOffset now);
}

public sealed class ActiveWorkflowRuns : IActiveWorkflowRuns
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Holding> _held = new();

    public string InstanceId { get; } =
        $"{Environment.MachineName}/{Environment.ProcessId}/{Guid.NewGuid().ToString("N")[..8]}";

    public IDisposable Hold(Guid workflowRunId)
    {
        lock (_gate)
        {
            if (!_held.TryGetValue(workflowRunId, out var holding))
            {
                holding = new Holding();
                _held[workflowRunId] = holding;
            }

            holding.Count++;
        }

        return new Release(this, workflowRunId);
    }

    public IReadOnlyCollection<Guid> Snapshot()
    {
        lock (_gate)
        {
            return _held.Keys.ToArray();
        }
    }

    public void Confirm(IEnumerable<Guid> workflowRunIds, DateTimeOffset leaseExpiresAt)
    {
        lock (_gate)
        {
            foreach (var id in workflowRunIds)
            {
                if (_held.TryGetValue(id, out var holding) && !holding.Revoked
                    && (holding.ConfirmedUntil is null || holding.ConfirmedUntil < leaseExpiresAt))
                {
                    holding.ConfirmedUntil = leaseExpiresAt;
                }
            }
        }
    }

    public void Revoke(Guid workflowRunId)
    {
        lock (_gate)
        {
            if (_held.TryGetValue(workflowRunId, out var holding))
            {
                holding.Revoked = true;
            }
        }
    }

    public bool HoldsLease(Guid workflowRunId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_held.TryGetValue(workflowRunId, out var holding))
            {
                return true;
            }

            return !holding.Revoked
                && (holding.ConfirmedUntil is not { } until || now < until - WorkflowRunLease.FenceMargin);
        }
    }

    private void Drop(Guid workflowRunId)
    {
        lock (_gate)
        {
            if (_held.TryGetValue(workflowRunId, out var holding) && --holding.Count <= 0)
            {
                _held.Remove(workflowRunId);
            }
        }
    }

    private sealed class Holding
    {
        public int Count { get; set; }

        public DateTimeOffset? ConfirmedUntil { get; set; }

        public bool Revoked { get; set; }
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
/// (stopped, crashed, or hung). The process itself stops acting for a run <see cref="FenceMargin"/> before its
/// confirmed lease lapses, so it has stopped before another instance can close the run; the margin also absorbs that
/// much clock difference between instances. A run from before leases existed counts as dead once it is older than
/// <see cref="LegacyGrace"/>.
/// </summary>
public static class WorkflowRunLease
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan FenceMargin = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan LegacyGrace = TimeSpan.FromMinutes(30);

    /// <summary>The error Execution History shows on a run whose process stopped before it finished.</summary>
    public const string InterruptedReason =
        "Interrupted: the process running this run stopped (restart, shutdown or crash) or stopped responding before " +
        "the run finished, so it did not complete. Nothing after the last completed step was done. Run it again.";

    /// <summary>The error a run gets when the process executing it stops because it could not keep its lease.</summary>
    public const string LeaseLostReason =
        "Stopped: this instance could not confirm it still owns the run (the database did not renew its lease in time, " +
        "or another instance closed the run), so it stopped before doing any more work. Check the run's EHR writes " +
        "before running it again.";
}

/// <summary>Thrown where a run must not go on (between nodes, before an EHR create) once
/// <see cref="IActiveWorkflowRuns.HoldsLease"/> is false.</summary>
public sealed class WorkflowRunLeaseLostException : InvalidOperationException
{
    public WorkflowRunLeaseLostException(Guid workflowRunId)
        : base(WorkflowRunLease.LeaseLostReason)
    {
        WorkflowRunId = workflowRunId;
    }

    public Guid WorkflowRunId { get; }
}
