namespace HealthAppBackend;

// Entities for the BackendSystem role's 11 read-only clinical tables in HealthAppDb. Every non-key column is
// nullable — these tables are populated by upstream FHIR-mapping pipelines that don't guarantee every field is
// present for every resource, so the model must reflect that rather than assume completeness (see requirement
// section 12, Null Handling). Table names/keys are configured in HealthAppDbContext.OnModelCreating.

public sealed class PatientNewMappedEntity
{
    public string PatientId { get; set; } = string.Empty;
    public string? Identifier { get; set; }
    public string? MRN { get; set; }
    public string? FamilyName { get; set; }
    public string? GivenName { get; set; }
    public string? MiddleName { get; set; }
    public string? Gender { get; set; }
    public DateOnly? BirthDate { get; set; }
    public bool? Deceased { get; set; }
    public string? MaritalStatus { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
}

public sealed class PractitionerEntity
{
    public string PractitionerId { get; set; } = string.Empty;
    public string? Identifier { get; set; }
    public string? NPI { get; set; }
    public string? FamilyName { get; set; }
    public string? GivenName { get; set; }
    public string? MiddleName { get; set; }
    public string? Gender { get; set; }
    public string? Qualification { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
}

public sealed class EncounterEntity
{
    public string EncounterId { get; set; } = string.Empty;
    public string? PatientId { get; set; }
    public string? PractitionerId { get; set; }
    public string? Identifier { get; set; }
    public string? Status { get; set; }
    public string? Class { get; set; }
    public string? Type { get; set; }
    public string? Priority { get; set; }
    public DateTime? StartDateTime { get; set; }
    public DateTime? EndDateTime { get; set; }
    public string? ServiceProvider { get; set; }
    public string? ReasonCode { get; set; }
}

public sealed class AllergyIntoleranceEntity
{
    public string AllergyIntoleranceId { get; set; } = string.Empty;
    public string? PatientId { get; set; }
    public string? EncounterId { get; set; }
    public string? Identifier { get; set; }
    public string? ClinicalStatus { get; set; }
    public string? VerificationStatus { get; set; }
    public string? Category { get; set; }
    public string? Criticality { get; set; }
    public string? Code { get; set; }
    public string? Substance { get; set; }
    public string? Reaction { get; set; }
    public string? Severity { get; set; }
    public DateTime? OnsetDateTime { get; set; }
    public DateTime? RecordedDate { get; set; }
}

public sealed class ObservationEntity
{
    public string ObservationId { get; set; } = string.Empty;
    public string? PatientId { get; set; }
    public string? EncounterId { get; set; }
    public string? Identifier { get; set; }
    public string? Status { get; set; }
    public string? Category { get; set; }
    public string? Code { get; set; }
    public string? Value { get; set; }
    public string? Unit { get; set; }
    public string? Interpretation { get; set; }
    public DateTime? EffectiveDateTime { get; set; }
    public DateTime? IssuedDateTime { get; set; }
}

public sealed class ConditionEntity
{
    public string ConditionId { get; set; } = string.Empty;
    public string? PatientId { get; set; }
    public string? EncounterId { get; set; }
    public string? Identifier { get; set; }
    public string? ClinicalStatus { get; set; }
    public string? VerificationStatus { get; set; }
    public string? Category { get; set; }
    public string? Severity { get; set; }
    public string? Code { get; set; }
    public string? BodySite { get; set; }
    public DateTime? OnsetDateTime { get; set; }
    public DateTime? AbatementDateTime { get; set; }
    public DateTime? RecordedDate { get; set; }
}

public sealed class ProcedureEntity
{
    public string ProcedureId { get; set; } = string.Empty;
    public string? PatientId { get; set; }
    public string? EncounterId { get; set; }
    public string? PractitionerId { get; set; }
    public string? Identifier { get; set; }
    public string? Status { get; set; }
    public string? Category { get; set; }
    public string? Code { get; set; }
    public string? BodySite { get; set; }
    public string? Outcome { get; set; }
    public DateTime? PerformedStartDateTime { get; set; }
    public DateTime? PerformedEndDateTime { get; set; }
}

public sealed class ServiceRequestEntity
{
    public string ServiceRequestId { get; set; } = string.Empty;
    public string? PatientId { get; set; }
    public string? EncounterId { get; set; }
    public string? PractitionerId { get; set; }
    public string? Identifier { get; set; }
    public string? Status { get; set; }
    public string? Intent { get; set; }
    public string? Priority { get; set; }
    public string? Category { get; set; }
    public string? Code { get; set; }
    public DateTime? AuthoredOn { get; set; }
    public DateTime? OccurrenceDateTime { get; set; }
    public string? ReasonCode { get; set; }
}

public sealed class DiagnosticReportEntity
{
    public string DiagnosticReportId { get; set; } = string.Empty;
    public string? PatientId { get; set; }
    public string? EncounterId { get; set; }
    public string? Identifier { get; set; }
    public string? Status { get; set; }
    public string? Category { get; set; }
    public string? Code { get; set; }
    public DateTime? EffectiveDateTime { get; set; }
    public DateTime? IssuedDateTime { get; set; }
    public string? Conclusion { get; set; }
}

public sealed class MedicationRequestEntity
{
    public string MedicationRequestId { get; set; } = string.Empty;
    public string? PatientId { get; set; }
    public string? EncounterId { get; set; }
    public string? PractitionerId { get; set; }
    public string? Identifier { get; set; }
    public string? Status { get; set; }
    public string? Intent { get; set; }
    public string? Priority { get; set; }
    public string? MedicationCode { get; set; }
    public string? DosageInstruction { get; set; }
    public DateTime? AuthoredOn { get; set; }
}

public sealed class MedicationAdministrationEntity
{
    public string MedicationAdministrationId { get; set; } = string.Empty;
    public string? PatientId { get; set; }
    public string? EncounterId { get; set; }
    public string? MedicationRequestId { get; set; }
    public string? Identifier { get; set; }
    public string? Status { get; set; }
    public string? MedicationCode { get; set; }
    public string? Dosage { get; set; }
    public string? Route { get; set; }
    public DateTime? EffectiveDateTime { get; set; }
    public string? Note { get; set; }
}
