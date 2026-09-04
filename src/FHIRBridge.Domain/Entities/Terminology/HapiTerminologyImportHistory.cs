namespace FHIRBridge.Domain.Entities.Terminology;

/// <summary>
/// One row per manual "Run Now" attempt against a HAPI-terminology-server sync (see
/// Infrastructure/Terminology/Hapi/HapiTerminologySystemRegistry). Shared across all 13 code systems via
/// <see cref="CodeSystem"/> rather than one table per system, since the shape is identical. Scheduled
/// worker runs do not write here today — only manual runs triggered from the portal.
/// </summary>
public sealed class HapiTerminologyImportHistory
{
    private HapiTerminologyImportHistory() { }
    public Guid Id { get; private set; } = Guid.NewGuid();
    public string CodeSystem { get; private set; } = default!;
    public string? Version { get; private set; }
    public DateTime StartedOnUtc { get; private set; }
    public DateTime? CompletedOnUtc { get; private set; }
    public int ImportedConceptCount { get; private set; }
    public string Status { get; private set; } = default!;
    public string? ErrorMessage { get; private set; }

    public HapiTerminologyImportHistory(string codeSystem)
    {
        Id = Guid.NewGuid(); CodeSystem = codeSystem; Status = "Running"; StartedOnUtc = DateTime.UtcNow;
    }

    public void Complete(int count, string? version)
    {
        ImportedConceptCount = count; Version = version; Status = "Succeeded"; CompletedOnUtc = DateTime.UtcNow;
    }

    public void Fail(string error) { Status = "Failed"; ErrorMessage = error; CompletedOnUtc = DateTime.UtcNow; }
}
