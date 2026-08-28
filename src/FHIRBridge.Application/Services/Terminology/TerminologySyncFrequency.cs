namespace FHIRBridge.Application.Services.Terminology;

/// <summary>Cadence options for a terminology code's scheduled sync, as read from its
/// <c>Terminology:&lt;Code&gt;:Frequency</c> system setting.</summary>
public enum TerminologySyncFrequency
{
    Daily,
    Weekly,
    Monthly,
}
