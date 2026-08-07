using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>
/// One field-level source-to-destination match, suggested by the schema matching engine or approved/rejected
/// by a user. Approved rows are the "learning" surface: a future request for the same
/// (SourceSystem, ResourceType, DestinationTable, DestinationField) short-circuits scoring entirely and
/// reuses this row's <see cref="SourceField"/> at confidence 1.0.
/// </summary>
public sealed class SchemaMapping : AuditableEntity<Guid>, IHasAuditDisplayName
{
    private SchemaMapping()
    {
    }

    public SchemaMapping(
        string sourceSystem,
        string resourceType,
        string destinationTable,
        string sourceField,
        string destinationField,
        double confidence,
        SchemaMappingStatus status = SchemaMappingStatus.Suggested)
    {
        Id = Guid.NewGuid();
        SourceSystem = sourceSystem;
        ResourceType = resourceType;
        DestinationTable = destinationTable;
        SourceField = sourceField;
        DestinationField = destinationField;
        Confidence = confidence;
        Status = status;
    }

    public string SourceSystem { get; private set; } = default!;
    public string ResourceType { get; private set; } = default!;
    public string DestinationTable { get; private set; } = default!;
    public string SourceField { get; private set; } = default!;
    public string DestinationField { get; private set; } = default!;
    public double Confidence { get; private set; }
    public SchemaMappingStatus Status { get; private set; }

    string? IHasAuditDisplayName.AuditDisplayName => $"{DestinationTable}.{DestinationField} ← {SourceField}";

    public void Approve()
    {
        Status = SchemaMappingStatus.Approved;
    }

    public void Reject()
    {
        Status = SchemaMappingStatus.Rejected;
    }

    /// <summary>Re-approving (or re-rejecting) an existing row against a different source field updates it in
    /// place rather than leaving a stale duplicate — <see cref="DestinationField"/> stays the natural key.</summary>
    public void UpdateMatch(string sourceField, double confidence, SchemaMappingStatus status)
    {
        SourceField = sourceField;
        Confidence = confidence;
        Status = status;
    }
}
