using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Destinations.EhrWriteBack.Epic;

namespace FHIRBridge.Infrastructure.Destinations.EhrWriteBack.Healow;

// eClinicalWorks (Healow) write profiles. eCW's clinical write APIs are contracted, not part of its certified FHIR
// server (whose CapabilityStatement lists only QuestionnaireResponse create), and their specifications are not
// public. Until a contracted spec and a practice sandbox are available, each profile shapes the US Core subset the
// verified Epic profile builds, and the capabilities keep every eCW type dry-run-only. See
// docs/backend/20-epic-r4-write-back.md section 11.

/// <summary>eCW AllergyIntolerance create (contracted, v12.0.2+).</summary>
public sealed class HealowAllergyIntoleranceWriteProfile : DelegatingEhrWriteProfile
{
    public HealowAllergyIntoleranceWriteProfile()
        : base(SourceSystemType.Healow, new EpicAllergyIntoleranceWriteProfile())
    {
    }
}

/// <summary>eCW Condition create (contracted, v12.0.2+). eCW files problems on an open telephone encounter it
/// manages itself, so the writer does not resolve one.</summary>
public sealed class HealowConditionWriteProfile : DelegatingEhrWriteProfile
{
    public HealowConditionWriteProfile()
        : base(SourceSystemType.Healow, new EpicConditionWriteProfile())
    {
    }
}

/// <summary>eCW Observation create for vital signs (contracted, v12.0.3.04009405+), LOINC-coded.</summary>
public sealed class HealowVitalSignWriteProfile : DelegatingEhrWriteProfile
{
    public HealowVitalSignWriteProfile()
        : base(SourceSystemType.Healow, new EpicVitalSignWriteProfile())
    {
    }
}

/// <summary>eCW Patient create (contracted, v12.0.2+). eCW refuses a patient whose account number and birth date
/// already match (error 202), which the capability records as already at target.</summary>
public sealed class HealowPatientWriteProfile : DelegatingEhrWriteProfile
{
    public HealowPatientWriteProfile()
        : base(SourceSystemType.Healow, new EpicPatientWriteProfile())
    {
    }
}
