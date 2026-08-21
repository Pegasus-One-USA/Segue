using System.Diagnostics;
using FHIRBridge.SharedKernel.Observability;

namespace FHIRBridge.Observability;

/// <summary>
/// Computes CPU% via the standard diff-against-last-sample technique (TotalProcessorTime delta over wall-clock
/// delta, normalized by core count) since .NET has no direct instantaneous CPU% API. Thread-safe; a singleton so
/// every request diffs against the previous request's sample rather than process start (which would flatten a
/// spike into a lifetime average).
/// </summary>
public sealed class ProcessHealthProvider : IProcessHealthProvider
{
    private readonly object _gate = new();
    private DateTime _lastSampleUtc;
    private TimeSpan _lastTotalProcessorTime;
    private bool _hasBaseline;

    public ProcessHealthSnapshot GetSnapshot()
    {
        using var process = Process.GetCurrentProcess();
        var nowUtc = DateTime.UtcNow;
        var totalProcessorTime = process.TotalProcessorTime;
        var workingSet = process.WorkingSet64;

        lock (_gate)
        {
            double? cpuPercent = null;

            if (_hasBaseline)
            {
                var wallClockDelta = nowUtc - _lastSampleUtc;
                var cpuTimeDelta = totalProcessorTime - _lastTotalProcessorTime;

                if (wallClockDelta > TimeSpan.Zero)
                {
                    cpuPercent = Math.Round(
                        100.0 * cpuTimeDelta.TotalMilliseconds / (wallClockDelta.TotalMilliseconds * Environment.ProcessorCount),
                        2);
                    cpuPercent = Math.Clamp(cpuPercent.Value, 0, 100);
                }
            }

            _lastSampleUtc = nowUtc;
            _lastTotalProcessorTime = totalProcessorTime;
            _hasBaseline = true;

            return new ProcessHealthSnapshot(cpuPercent, workingSet);
        }
    }
}
