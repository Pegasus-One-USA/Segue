namespace FHIRBridge.Domain.Enums;

/// <summary>Which CDS Hooks endpoint a <see cref="Entities.Governance.CdsCallLog"/> row records.</summary>
public enum CdsCallKind
{
    /// <summary>POST /cds-services/{id}: the EHR asked for cards.</summary>
    Invoke = 1,

    /// <summary>POST /cds-services/{id}/feedback: the EHR reported what the clinician did with the cards.</summary>
    Feedback = 2,
}
