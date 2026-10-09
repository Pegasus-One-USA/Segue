using System.Text.Json.Nodes;

namespace FHIRBridge.Application.Services.Tabular;

/// <summary>
/// Starting templates, one per resource type the EHR write-back accepts, with conventional column names. They are
/// edited in the source form; nothing at run time depends on them. Every one keys the resource on a source id
/// column, because EHR write-back needs a stable id per record to avoid filing it twice.
/// </summary>
public static class TabularTemplatePresets
{
    public static IReadOnlyDictionary<string, string> All { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Patient"] = """
            {
              "resourceType": "Patient",
              "id": "{{patient_id}}",
              "identifier": [{ "system": "{{mrn_system}}", "value": "{{mrn}}" }],
              "name": [{ "use": "official", "family": "{{last_name}}", "given": ["{{first_name}}", "{{middle_name}}"] }],
              "gender": "{{gender|lower}}",
              "birthDate": "{{birth_date|date}}",
              "telecom": [
                { "system": "phone", "value": "{{phone}}", "use": "home" },
                { "system": "email", "value": "{{email}}" }
              ],
              "address": [{
                "use": "home", "line": ["{{address_line1}}", "{{address_line2}}"], "city": "{{city}}",
                "state": "{{state}}", "postalCode": "{{postal_code}}", "country": "{{country}}"
              }]
            }
            """,
        ["AllergyIntolerance"] = """
            {
              "resourceType": "AllergyIntolerance",
              "id": "{{allergy_id}}",
              "clinicalStatus": { "coding": [{ "system": "http://terminology.hl7.org/CodeSystem/allergyintolerance-clinical", "code": "active" }] },
              "code": {
                "coding": [{ "system": "http://www.nlm.nih.gov/research/umls/rxnorm", "code": "{{rxnorm_code}}" }],
                "text": "{{allergen}}"
              },
              "patient": { "reference": "Patient/{{patient_id}}" },
              "onsetDateTime": "{{onset_date|date}}",
              "reaction": [{ "manifestation": [{ "text": "{{reaction}}" }] }]
            }
            """,
        ["Condition"] = """
            {
              "resourceType": "Condition",
              "id": "{{problem_id}}",
              "clinicalStatus": { "coding": [{ "system": "http://terminology.hl7.org/CodeSystem/condition-clinical", "code": "active" }] },
              "category": [{ "coding": [{ "system": "http://terminology.hl7.org/CodeSystem/condition-category", "code": "problem-list-item" }] }],
              "code": {
                "coding": [
                  { "system": "http://snomed.info/sct", "code": "{{snomed_code}}" },
                  { "system": "http://hl7.org/fhir/sid/icd-10-cm", "code": "{{icd10_code}}" }
                ],
                "text": "{{problem}}"
              },
              "subject": { "reference": "Patient/{{patient_id}}" },
              "onsetDateTime": "{{onset_date|date}}"
            }
            """,
        ["Observation"] = """
            {
              "resourceType": "Observation",
              "id": "{{vital_id}}",
              "status": "final",
              "category": [{ "coding": [{ "system": "http://terminology.hl7.org/CodeSystem/observation-category", "code": "vital-signs" }] }],
              "code": { "coding": [{ "system": "http://loinc.org", "code": "{{loinc_code}}" }], "text": "{{vital_name}}" },
              "subject": { "reference": "Patient/{{patient_id}}" },
              "effectiveDateTime": "{{taken_at|datetime}}",
              "valueQuantity": { "value": "{{value|number}}", "unit": "{{unit}}", "system": "http://unitsofmeasure.org", "code": "{{unit}}" }
            }
            """,
        ["DocumentReference"] = """
            {
              "resourceType": "DocumentReference",
              "id": "{{note_id}}",
              "status": "current",
              "type": { "coding": [{ "system": "http://loinc.org", "code": "{{note_loinc_code}}" }] },
              "category": [{ "coding": [{ "system": "http://hl7.org/fhir/us/core/CodeSystem/us-core-documentreference-category", "code": "clinical-note" }] }],
              "subject": { "reference": "Patient/{{patient_id}}" },
              "date": "{{note_date|datetime}}",
              "content": [{ "attachment": { "contentType": "text/plain", "data": "{{note_text|base64}}" } }]
            }
            """,
    };

    /// <summary>The stored-templates JSON (<c>[{resourceType, template}]</c>) for the given preset types.</summary>
    public static string ToStoredJson(IEnumerable<string> resourceTypes)
    {
        var array = new JsonArray();
        foreach (var type in resourceTypes)
        {
            if (All.TryGetValue(type, out var template))
            {
                array.Add(new JsonObject { ["resourceType"] = type, ["template"] = JsonNode.Parse(template) });
            }
        }

        return array.ToJsonString();
    }
}
