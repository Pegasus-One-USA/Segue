using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Infrastructure.Terminology;

/// <summary>
/// In-process catalog of the small, closed FHIR/US Core required-binding ValueSets (statuses, gender, intent). These
/// are fully enumerable offline, so codes can be validated and expanded without a terminology server. Anything not in
/// the catalog falls through to the FHIR <c>$validate-code</c>/<c>$expand</c> server.
/// </summary>
public static class UsCoreValueSetCatalog
{
    // Canonical ValueSet URLs (used as keys and as the binding references on resources).
    public const string AdministrativeGender = "http://hl7.org/fhir/ValueSet/administrative-gender";
    public const string ObservationStatus = "http://hl7.org/fhir/ValueSet/observation-status";
    public const string ConditionClinicalStatus = "http://hl7.org/fhir/ValueSet/condition-clinical";
    public const string EncounterStatus = "http://hl7.org/fhir/ValueSet/encounter-status";
    public const string MedicationRequestStatus = "http://hl7.org/fhir/ValueSet/medicationrequest-status";
    public const string MedicationRequestIntent = "http://hl7.org/fhir/ValueSet/medicationrequest-intent";
    public const string ImmunizationStatus = "http://hl7.org/fhir/ValueSet/immunization-status";
    public const string EventStatus = "http://hl7.org/fhir/ValueSet/event-status";
    public const string DiagnosticReportStatus = "http://hl7.org/fhir/ValueSet/diagnostic-report-status";
    // Tier-2 / Tier-3 resource status bindings.
    public const string FinancialResourceStatus = "http://hl7.org/fhir/ValueSet/fm-status";
    public const string ExplanationOfBenefitStatus = "http://hl7.org/fhir/ValueSet/explanationofbenefit-status";
    public const string RequestStatus = "http://hl7.org/fhir/ValueSet/request-status";
    public const string GoalLifecycleStatus = "http://hl7.org/fhir/ValueSet/goal-status";
    public const string DocumentReferenceStatus = "http://hl7.org/fhir/ValueSet/document-reference-status";
    public const string GroupType = "http://hl7.org/fhir/ValueSet/group-type";
    public const string MeasureReportStatus = "http://hl7.org/fhir/ValueSet/measure-report-status";
    public const string SubscriptionStatus = "http://hl7.org/fhir/ValueSet/subscription-status";
    public const string LocationStatus = "http://hl7.org/fhir/ValueSet/location-status";
    public const string MedicationAdministrationStatus = "http://hl7.org/fhir/ValueSet/medication-admin-status";

    private const string GenderSystem = "http://hl7.org/fhir/administrative-gender";

    private static readonly IReadOnlyDictionary<string, ValueSetDefinition> Catalog =
        new Dictionary<string, ValueSetDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [AdministrativeGender] = Vs(GenderSystem, "male", "female", "other", "unknown"),
            [ObservationStatus] = Vs("http://hl7.org/fhir/observation-status",
                "registered", "preliminary", "final", "amended", "corrected", "cancelled", "entered-in-error", "unknown"),
            [ConditionClinicalStatus] = Vs("http://terminology.hl7.org/CodeSystem/condition-clinical",
                "active", "recurrence", "relapse", "inactive", "remission", "resolved"),
            [EncounterStatus] = Vs("http://hl7.org/fhir/encounter-status",
                "planned", "arrived", "triaged", "in-progress", "onleave", "finished", "cancelled", "entered-in-error", "unknown"),
            [MedicationRequestStatus] = Vs("http://hl7.org/fhir/CodeSystem/medicationrequest-status",
                "active", "on-hold", "cancelled", "completed", "entered-in-error", "stopped", "draft", "unknown"),
            [MedicationRequestIntent] = Vs("http://hl7.org/fhir/CodeSystem/medicationrequest-intent",
                "proposal", "plan", "order", "original-order", "reflex-order", "filler-order", "instance-order", "option"),
            [ImmunizationStatus] = Vs("http://hl7.org/fhir/event-status",
                "completed", "entered-in-error", "not-done"),
            [EventStatus] = Vs("http://hl7.org/fhir/event-status",
                "preparation", "in-progress", "not-done", "on-hold", "stopped", "completed", "entered-in-error", "unknown"),
            [DiagnosticReportStatus] = Vs("http://hl7.org/fhir/diagnostic-report-status",
                "registered", "partial", "preliminary", "final", "amended", "corrected", "appended", "cancelled", "entered-in-error", "unknown"),
            // Tier-2 / Tier-3 resource status bindings.
            [FinancialResourceStatus] = Vs("http://hl7.org/fhir/fm-status",
                "active", "cancelled", "draft", "entered-in-error"),
            [ExplanationOfBenefitStatus] = Vs("http://hl7.org/fhir/explanationofbenefit-status",
                "active", "cancelled", "draft", "entered-in-error"),
            [RequestStatus] = Vs("http://hl7.org/fhir/request-status",
                "draft", "active", "on-hold", "revoked", "completed", "entered-in-error", "unknown"),
            [GoalLifecycleStatus] = Vs("http://hl7.org/fhir/goal-status",
                "proposed", "planned", "accepted", "active", "on-hold", "completed", "cancelled", "entered-in-error", "rejected"),
            [DocumentReferenceStatus] = Vs("http://hl7.org/fhir/document-reference-status",
                "current", "superseded", "entered-in-error"),
            [GroupType] = Vs("http://hl7.org/fhir/group-type",
                "person", "animal", "practitioner", "device", "medication", "substance"),
            [MeasureReportStatus] = Vs("http://hl7.org/fhir/measure-report-status",
                "complete", "pending", "error"),
            [SubscriptionStatus] = Vs("http://hl7.org/fhir/subscription-status",
                "requested", "active", "error", "off"),
            [LocationStatus] = Vs("http://hl7.org/fhir/location-status",
                "active", "suspended", "inactive"),
            [MedicationAdministrationStatus] = Vs("http://hl7.org/fhir/CodeSystem/medication-admin-status",
                "in-progress", "not-done", "on-hold", "completed", "entered-in-error", "stopped", "unknown"),
        };

    public static bool TryGet(string valueSetUrl, out ValueSetDefinition definition)
        => Catalog.TryGetValue(valueSetUrl, out definition!);

    public sealed record ValueSetDefinition(string System, IReadOnlySet<string> Codes)
    {
        public IReadOnlyList<TerminologyConcept> ToConcepts()
            => Codes.Select(code => new TerminologyConcept(System, code, null)).ToList();
    }

    private static ValueSetDefinition Vs(string system, params string[] codes)
        => new(system, new HashSet<string>(codes, StringComparer.OrdinalIgnoreCase));
}
