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

/// <summary>
/// CodeSystems whose defined codes are stable across FHIR R4/R4B/R5 — safe to omit <c>Coding.version</c> on when
/// writing to a destination FHIR server, so it matches by <c>system</c> alone instead of rejecting on a
/// version-label mismatch (e.g. Epic tagging <c>4.0.0</c> against a destination's <c>3.0.0</c>-pinned CodeSystem).
/// Deliberately excludes CodeSystems verified to have codes actually removed or renamed between FHIR versions (not
/// just added) — a version mismatch on these is a genuine signal worth surfacing rather than stripping away:
/// <list type="bullet">
/// <item><c>encounter-status</c> — dropped <c>onleave</c>/<c>finished</c>, added <c>on-hold</c>/<c>discharged</c>/
/// <c>completed</c>/<c>discontinued</c> in R5.</item>
/// <item><c>group-type</c> — dropped <c>medication</c>/<c>substance</c> in R5.</item>
/// </list>
/// By contrast, <c>allergyintolerance-verification</c> (R5 adds a 5th code, <c>presumed</c>) and
/// <c>composition-status</c> (R5 adds new parent/sibling codes around the original 4, none removed) are additive-only
/// changes — same category as the <c>medicationrequest-status</c>/<c>diagnostic-report-status</c>/
/// <c>subscription-status</c> entries below — and are included, since a source that only emits R4-era codes can never
/// produce a code invalidated by an additive R5 change.
/// </summary>
public static class StableCodeSystemVersions
{
    public static readonly IReadOnlySet<string> StableCodeSystemUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Verified stable across R4/R4B/R5 (from UsCoreValueSetCatalog, minus encounter-status/group-type):
        "http://hl7.org/fhir/administrative-gender",
        "http://hl7.org/fhir/observation-status",
        "http://terminology.hl7.org/CodeSystem/condition-clinical",
        "http://hl7.org/fhir/CodeSystem/medicationrequest-intent",
        "http://hl7.org/fhir/event-status",
        "http://hl7.org/fhir/fm-status",
        "http://hl7.org/fhir/explanationofbenefit-status",
        "http://hl7.org/fhir/request-status",
        "http://hl7.org/fhir/goal-status",
        "http://hl7.org/fhir/document-reference-status",
        "http://hl7.org/fhir/measure-report-status",
        "http://hl7.org/fhir/location-status",
        "http://hl7.org/fhir/CodeSystem/medication-admin-status",
        // Additive-only R4->R5 changes (source only emits R4-era codes, so safe in this direction):
        "http://hl7.org/fhir/CodeSystem/medicationrequest-status",
        "http://hl7.org/fhir/diagnostic-report-status",
        "http://hl7.org/fhir/subscription-status",
        "http://terminology.hl7.org/CodeSystem/allergyintolerance-verification",
        "http://hl7.org/fhir/composition-status",
        // Confirmed by an Epic repro sample to also carry version tags (same bug, different fields):
        "http://terminology.hl7.org/CodeSystem/condition-ver-status",
        "http://terminology.hl7.org/CodeSystem/condition-category",
        // Verified stable across R4/R4B/R5 (simple closed code sets):
        "http://terminology.hl7.org/CodeSystem/allergyintolerance-clinical",
        "http://hl7.org/fhir/request-priority",
        "http://hl7.org/fhir/claim-use",
        "http://hl7.org/fhir/claim-type",
        "http://hl7.org/fhir/name-use",
        "http://hl7.org/fhir/contact-point-use",
        "http://hl7.org/fhir/address-use",
        "http://hl7.org/fhir/identifier-use",
    };
}
