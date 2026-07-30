namespace HealthAppBackend;

// Entities for the "_11" curated landing tables (Patient_11 .. Procedure_11) in HealthAppDb — the business/layman
// view designed from the FHIR sample export. Read-only here: rows are populated by an external load process, not by
// this app. Every non-key column is nullable (these tables are filled by upstream FHIR-mapping pipelines that don't
// guarantee every column for every row). Table names/keys are configured in HealthAppDbContext.OnModelCreating.
// Column types deliberately mirror "9 resource tables _11 (curated).sql" so a table created by that DDL and one
// created by EF's EnsureCreated (fresh database) are read-compatible.

public sealed class Patient11Entity
{
    public string PatientId { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Gender { get; set; }
    public DateOnly? BirthDate { get; set; }
    public string? MaritalStatus { get; set; }
    public string? Race { get; set; }
    public string? Ethnicity { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? AddressLine { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? PreferredLanguage { get; set; }
    public bool? IsDeceased { get; set; }
    public string? MedicalRecordNumber { get; set; }
    public string? PrimaryCareProviderId { get; set; }
    public string? PrimaryCareProviderName { get; set; }
    public string? OrganizationId { get; set; }
    public string? OrganizationName { get; set; }
}

public sealed class Practitioner11Entity
{
    public string PractitionerId { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Title { get; set; }
    public string? Credential { get; set; }
    public string? Specialty { get; set; }
    public string? Gender { get; set; }
    public string? NPI { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? AddressLine { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }
    public bool? IsActive { get; set; }
}

public sealed class Encounter11Entity
{
    public string EncounterId { get; set; } = string.Empty;
    public string? PatientId { get; set; }
    public string? PatientName { get; set; }
    public string? EncounterType { get; set; }
    public string? EncounterClass { get; set; }
    public string? Status { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public string? ReasonForVisit { get; set; }
    public string? LocationName { get; set; }
    public string? AttendingProviderId { get; set; }
    public string? AttendingProviderName { get; set; }
    public string? OrganizationId { get; set; }
    public string? OrganizationName { get; set; }
}

public sealed class Observation11Entity
{
    public string ObservationId { get; set; } = string.Empty;
    public string? PatientId { get; set; }
    public string? EncounterId { get; set; }
    public string? Category { get; set; }
    public string? ObservationName { get; set; }
    public decimal? NumericValue { get; set; }
    public string? Unit { get; set; }
    public string? TextValue { get; set; }
    public DateTime? EffectiveDateTime { get; set; }
    public string? Status { get; set; }
    public string? Interpretation { get; set; }
    public string? ReferenceRange { get; set; }
    public string? PerformedById { get; set; }
    public string? PerformedByName { get; set; }
}

public sealed class Condition11Entity
{
    public string ConditionId { get; set; } = string.Empty;
    public string? PatientId { get; set; }
    public string? EncounterId { get; set; }
    public string? ConditionName { get; set; }
    public string? DiagnosisCode { get; set; }
    public string? Category { get; set; }
    public string? ClinicalStatus { get; set; }
    public string? Severity { get; set; }
    public DateOnly? OnsetDate { get; set; }
    public DateOnly? RecordedDate { get; set; }
    public string? RecordedById { get; set; }
    public string? RecordedByName { get; set; }
}

public sealed class AllergyIntolerance11Entity
{
    public string AllergyId { get; set; } = string.Empty;
    public string? PatientId { get; set; }
    public string? Allergen { get; set; }
    public string? Category { get; set; }
    public string? Type { get; set; }
    public string? Criticality { get; set; }
    public string? ClinicalStatus { get; set; }
    public string? Reaction { get; set; }
    // NVARCHAR in the DDL — the source sometimes carries just a year, not a full date.
    public string? OnsetDate { get; set; }
    public DateTime? RecordedDate { get; set; }
}

public sealed class MedicationRequest11Entity
{
    public string MedicationRequestId { get; set; } = string.Empty;
    public string? PatientId { get; set; }
    public string? EncounterId { get; set; }
    public string? MedicationId { get; set; }
    public string? MedicationName { get; set; }
    public string? Status { get; set; }
    public string? DosageInstructions { get; set; }
    public string? Route { get; set; }
    public decimal? DoseQuantity { get; set; }
    public string? DoseUnit { get; set; }
    public string? Frequency { get; set; }
    public decimal? DispenseQuantity { get; set; }
    public string? DispenseUnit { get; set; }
    public int? Refills { get; set; }
    public int? DaysSupply { get; set; }
    public DateOnly? PrescribedDate { get; set; }
    public string? Reason { get; set; }
    public string? PrescriberId { get; set; }
    public string? PrescriberName { get; set; }
}

public sealed class MedicationAdministration11Entity
{
    public string MedicationAdministrationId { get; set; } = string.Empty;
    public string? PatientId { get; set; }
    public string? EncounterId { get; set; }
    public string? MedicationRequestId { get; set; }
    public string? MedicationId { get; set; }
    public string? MedicationName { get; set; }
    public string? Status { get; set; }
    public DateTime? AdministeredDateTime { get; set; }
    public DateTime? AdministeredStartTime { get; set; }
    public DateTime? AdministeredEndTime { get; set; }
    public decimal? DoseQuantity { get; set; }
    public string? DoseUnit { get; set; }
    public string? Route { get; set; }
    public string? Method { get; set; }
    public string? BodySite { get; set; }
    public string? Reason { get; set; }
    public string? Notes { get; set; }
    public string? AdministeredById { get; set; }
    public string? AdministeredByName { get; set; }
}

public sealed class ServiceRequest11Entity
{
    public string ServiceRequestId { get; set; } = string.Empty;
    public string? PatientId { get; set; }
    public string? EncounterId { get; set; }
    public string? OrderName { get; set; }
    public string? OrderCode { get; set; }
    public string? Category { get; set; }
    public string? Status { get; set; }
    public string? Reason { get; set; }
    public DateTime? OrderedDate { get; set; }
    public DateTime? ScheduledDate { get; set; }
    public string? OrderedById { get; set; }
    public string? OrderedByName { get; set; }
}

public sealed class DiagnosticReport11Entity
{
    public string DiagnosticReportId { get; set; } = string.Empty;
    public string? PatientId { get; set; }
    public string? EncounterId { get; set; }
    public string? ServiceRequestId { get; set; }
    public string? ReportName { get; set; }
    public string? Category { get; set; }
    public string? Status { get; set; }
    public DateTime? EffectiveDateTime { get; set; }
    public DateTime? IssuedDateTime { get; set; }
    public string? Conclusion { get; set; }
    public string? ReportDocumentUrl { get; set; }
    public string? PerformedById { get; set; }
    public string? PerformedByName { get; set; }
}

public sealed class Procedure11Entity
{
    public string ProcedureId { get; set; } = string.Empty;
    public string? PatientId { get; set; }
    public string? EncounterId { get; set; }
    public string? ServiceRequestId { get; set; }
    public string? DiagnosticReportId { get; set; }
    public string? ProcedureName { get; set; }
    public string? ProcedureCode { get; set; }
    public string? Category { get; set; }
    public string? Status { get; set; }
    public DateTime? PerformedDate { get; set; }
    public string? BodySite { get; set; }
    public string? Reason { get; set; }
    public string? LocationName { get; set; }
    public string? PerformedById { get; set; }
    public string? PerformedByName { get; set; }
}

// Per-role workflow URL for the "New 11" menu — one row per non-Admin role (Patient, ProviderStandalone,
// ProviderInApp, BackendSystem). Admin-configured (see the Admin settings "New 11" tab) and used to import
// referenced-but-missing Practitioners into Practitioner_11. The table is ensured + seeded at startup in
// Program.cs (EnsureCreated won't add it to an already-existing HealthAppDb).
public sealed class Resource11WorkflowSettingEntity
{
    public string Role { get; set; } = string.Empty;
    public string WorkflowUrl { get; set; } = string.Empty;
}
