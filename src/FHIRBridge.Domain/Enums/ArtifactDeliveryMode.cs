namespace FHIRBridge.Domain.Enums;

/// <summary>
/// How a generated export file (CSV today; any future format) reaches its consumer. Format-agnostic on purpose —
/// a writer serializes its own format into a <c>GeneratedFile</c>, then hands it to the delivery strategy for this
/// mode, so Excel/PDF/XML writers can reuse the exact same four delivery paths without new per-format plumbing.
/// </summary>
public enum ArtifactDeliveryMode
{
    Download = 0,
    Email = 1,
    Sftp = 2,
    DownloadUrl = 3
}
