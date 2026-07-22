namespace FHIRBridge.Governance;

/// <summary>
/// Classification of a captured exception, assigned by the Global Exception Manager. Persisted by name
/// (string) on the ErrorLog record so the enum can grow without a schema change, and surfaced as a filter
/// on the Monitoring → Errors screen. Lives in the Governance building block (not Domain) because the
/// classifier that produces it is a cross-cutting concern referenced by every layer.
/// </summary>
public enum ErrorCategory
{
    Unknown = 0,
    Business = 1,
    Validation = 2,
    Infrastructure = 3,
    Authentication = 4,
    Authorization = 5,
    Database = 6,
    Network = 7,
    ExternalSystem = 8,
}
