namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// The JSON an athenahealth write profile shapes a record into when athena's FHIR API cannot take it: a description of
/// one athenaOne REST call (<c>/v1/{practiceid}/...</c>), which the athenaOne write channel sends. Shaped by the
/// profile (Infrastructure), sent by the channel (Runtime), so the property names live here, where both see them.
///
/// <code>
/// {
///   "resourceType": "AthenaOneRequest",
///   "operation":   "problem",                         // short name, for logs and synthetic ids
///   "method":      "POST",
///   "path":        "chart/{patientid}/problems",      // relative to /v1/{practiceid}/; {patientid}, {encounterid}
///   "multipart":   false,                             // form-urlencoded unless true
///   "department":  true,                              // add departmentid (destination's, else the patient's)
///   "fields":      { "snomedcode": "38341003" },      // plain form fields
///   "jsonFields":  { "allergies": [ ... ] },          // form fields whose value is JSON text
///   "lookups":     [ { "kind": "allergen", "name": "Penicillin", "token": "{{allergenid}}" } ],
///   "existing":    { "path": "chart/{patientid}/allergies", "list": "allergies",
///                    "match": "allergenid", "value": "{{allergenid}}" },  // optional: read first, skip if present
///   "idField":     "problemid",                       // response field holding the new id; absent when none
///   "patient":     "a-195900.E-1234",                 // set by BindReferences: target patient (FHIR or athena id)
///   "encounter":   "56789"                            // set by BindReferences when the call needs one
/// }
/// </code>
///
/// <para>A lookup replaces its token, wherever it appears in a field value, by the athena id the reference API returns
/// for an exact (case-insensitive) name match; no exact match refuses the record before anything is sent.</para>
///
/// <para>An <c>existing</c> check reads the patient's chart list at its path (with the department) once the lookups
/// are filled in: when an entry's <c>match</c> field equals <c>value</c> (a lookup token, or a literal), the record is
/// already on the chart and is reported as <see cref="AlreadyOnChartCode"/> without being sent. An upsert such as the
/// allergy PUT would otherwise replace what a clinician entered.</para>
/// </summary>
public static class AthenaOneWriteRequest
{
    public const string ResourceType = "AthenaOneRequest";
    public const string Operation = "operation";
    public const string Method = "method";
    public const string Path = "path";
    public const string Multipart = "multipart";
    public const string Department = "department";
    public const string Fields = "fields";
    public const string JsonFields = "jsonFields";
    public const string Lookups = "lookups";
    public const string LookupKind = "kind";
    public const string LookupName = "name";
    public const string LookupToken = "token";
    public const string IdField = "idField";
    public const string Patient = "patient";
    public const string Encounter = "encounter";
    public const string Existing = "existing";
    public const string ExistingPath = "path";
    public const string ExistingList = "list";
    public const string ExistingMatch = "match";
    public const string ExistingValue = "value";

    /// <summary>The outcome code of a record the <c>existing</c> check found on the chart already; the athenahealth
    /// vendor profile counts it as already at target (EhrWriteCapabilities).</summary>
    public const string AlreadyOnChartCode = "athena-already-on-chart";

    public const string PatientIdToken = "{patientid}";
    public const string EncounterIdToken = "{encounterid}";

    /// <summary>Lookup kinds the channel resolves through athenaOne's reference APIs.</summary>
    public const string AllergenLookup = "allergen";
    public const string MedicationLookup = "medication";
}
