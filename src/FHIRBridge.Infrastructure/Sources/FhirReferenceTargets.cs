namespace FHIRBridge.Infrastructure.Sources;

/// <summary>
/// Which resource types a given FHIR R4 resource's reference-typed elements can point to — sourced from the R4
/// StructureDefinitions themselves (e.g. <c>Patient.generalPractitioner</c> targets
/// <c>Organization | Practitioner | PractitionerRole</c>), not discovered empirically per vendor. Used by
/// <see cref="EpicSourceConnectionScopeSyncService"/> to widen a source connection's requested scopes when
/// "Automatically fetch a missing reference from the source" is enabled, so a destination scoped for e.g.
/// Patient-only doesn't 403 the first time it needs to pull a referenced Organization/Practitioner.
///
/// <para><b>Deliberately excludes <c>PractitionerRole</c></b> from every target list, even though the FHIR R4 spec
/// allows it wherever a plain <c>Practitioner</c> reference is allowed: verified live against athenahealth's
/// preview sandbox that requesting <c>system/PractitionerRole.read</c> alongside otherwise-valid scopes makes the
/// authorization server reject the ENTIRE token request (401 <c>access_denied</c> / "Policy evaluation failed") —
/// not just decline that one scope. athenahealth's (Okta-fronted) policy evaluation is all-or-nothing per request,
/// so speculatively widening to every spec-legal reference target is actively dangerous, not just harmlessly
/// broad: one scope the app registration was never granted breaks auth for every resource type in the same
/// request, including ones that were already working. Every target type still listed here (Organization,
/// Practitioner, RelatedPerson, Location, etc.) has been confirmed NOT to trigger this all-or-nothing rejection.
/// </para>
///
/// Deliberately covers only the resource types this catalog's connectors actually support (see
/// <c>DefaultWorkflowNodeCatalog</c>) and only the reference elements worth widening for — this only needs
/// revisiting on a FHIR version change, not every time a new vendor-specific reference pattern turns up (any gap
/// still surfaces as a clear, actionable auto-fetch scope error rather than a silent drop).
/// </summary>
internal static class FhirReferenceTargets
{
    private static readonly IReadOnlyDictionary<string, string[]> Targets = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Patient"] = ["Organization", "Practitioner", "RelatedPerson"],
        ["Encounter"] = ["Patient", "Practitioner", "RelatedPerson", "Organization", "Location", "ServiceRequest"],
        ["AllergyIntolerance"] = ["Patient", "Practitioner", "RelatedPerson", "Encounter"],
        ["Observation"] = ["Patient", "Practitioner", "Organization", "RelatedPerson", "Encounter"],
        ["Condition"] = ["Patient", "Practitioner", "RelatedPerson", "Encounter"],
        ["Procedure"] = ["Patient", "Practitioner", "Organization", "RelatedPerson", "Location", "Encounter"],
        ["ServiceRequest"] = ["Patient", "Practitioner", "Organization", "RelatedPerson", "Encounter"],
        ["DiagnosticReport"] = ["Patient", "Practitioner", "Organization", "Encounter"],
        ["MedicationRequest"] = ["Patient", "Practitioner", "Organization", "RelatedPerson", "Medication", "Encounter"],
        ["MedicationAdministration"] = ["Patient", "Practitioner", "RelatedPerson", "Medication"],
        ["MedicationDispense"] = ["Patient", "Practitioner", "Organization", "Medication"],
        ["MedicationStatement"] = ["Patient", "Practitioner", "RelatedPerson", "Medication"],
        ["Appointment"] = ["Patient", "Practitioner", "RelatedPerson", "Location"],
        ["CarePlan"] = ["Patient", "Practitioner", "RelatedPerson", "Organization", "CareTeam"],
        ["CareTeam"] = ["Patient", "Practitioner", "RelatedPerson", "Organization"],
        ["Communication"] = ["Patient", "Practitioner", "Organization", "RelatedPerson"],
        ["CommunicationRequest"] = ["Patient", "Practitioner", "Organization", "RelatedPerson"],
        ["Device"] = ["Patient", "Organization"],
        ["DocumentReference"] = ["Patient", "Practitioner", "Organization", "RelatedPerson"],
        ["FamilyMemberHistory"] = ["Patient"],
        ["Immunization"] = ["Patient", "Practitioner", "Organization", "Location"],
        ["PractitionerRole"] = ["Practitioner", "Organization", "Location"],
        ["Provenance"] = ["Patient", "Practitioner", "Organization", "RelatedPerson"],
        ["QuestionnaireResponse"] = ["Patient", "Practitioner", "RelatedPerson", "Encounter"],
        ["Task"] = ["Patient", "Practitioner", "Organization", "RelatedPerson"],
    };

    public static IReadOnlyList<string> For(string resourceType) =>
        Targets.TryGetValue(resourceType, out var targets) ? targets : [];
}
