namespace FHIRBridge.Worker;

/// <summary>
/// Options for the scheduled audit-chain verification worker role. Enabled by default so tampering with the
/// HIPAA audit trail is actually detected between Compliance Report runs, not only when someone happens to
/// generate one — closing the gap POL-010 §3.2 flags ("[ORGANIZATION TO COMPLETE — e.g., weekly]").
/// </summary>
public sealed class AuditChainVerificationOptions
{
    public bool Enabled { get; set; } = true;

    public int IntervalHours { get; set; } = 24;
}
