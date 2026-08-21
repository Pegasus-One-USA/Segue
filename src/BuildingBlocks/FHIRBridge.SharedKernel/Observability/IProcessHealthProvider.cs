namespace FHIRBridge.SharedKernel.Observability;

/// <summary>Live CPU/memory for the current host process — backs the System Health screen's "this process" row.</summary>
public interface IProcessHealthProvider
{
    ProcessHealthSnapshot GetSnapshot();
}

/// <param name="CpuPercent">Process CPU usage since the previous sample, as a percentage of total available
/// capacity (0-100 across all cores). Null on the very first sample (no prior baseline to diff against).</param>
public sealed record ProcessHealthSnapshot(double? CpuPercent, long WorkingSetBytes);
