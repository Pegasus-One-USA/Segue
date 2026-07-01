namespace FHIRBridge.Runtime.Infrastructure.Connectors;

internal static class SampleResources
{
    public static string GetResourceJson(string resourceType)
    {
        return resourceType switch
        {
            "Patient" => """
                {"resourceType":"Patient","id":"patient-phase1","meta":{"versionId":"1","lastUpdated":"2026-06-09T00:00:00Z"},"identifier":[{"system":"urn:fhirbridge:sample","value":"patient-phase1"}],"active":true}
                """,
            "Observation" => """
                {"resourceType":"Observation","id":"observation-phase1","meta":{"versionId":"1","lastUpdated":"2026-06-09T00:00:00Z"},"status":"final","code":{"coding":[{"system":"http://loinc.org","code":"8302-2"}]},"subject":{"reference":"Patient/patient-phase1"}}
                """,
            "Condition" => """
                {"resourceType":"Condition","id":"condition-phase1","meta":{"versionId":"1","lastUpdated":"2026-06-09T00:00:00Z"},"clinicalStatus":{"coding":[{"system":"http://terminology.hl7.org/CodeSystem/condition-clinical","code":"active"}]},"subject":{"reference":"Patient/patient-phase1"}}
                """,
            "MedicationRequest" => """
                {"resourceType":"MedicationRequest","id":"medicationrequest-phase1","meta":{"versionId":"1","lastUpdated":"2026-06-09T00:00:00Z"},"status":"active","intent":"order","subject":{"reference":"Patient/patient-phase1"}}
                """,
            "AllergyIntolerance" => """
                {"resourceType":"AllergyIntolerance","id":"allergyintolerance-phase1","meta":{"versionId":"1","lastUpdated":"2026-06-09T00:00:00Z"},"clinicalStatus":{"coding":[{"system":"http://terminology.hl7.org/CodeSystem/allergyintolerance-clinical","code":"active"}]},"patient":{"reference":"Patient/patient-phase1"}}
                """,
            "Encounter" => """
                {"resourceType":"Encounter","id":"encounter-phase1","meta":{"versionId":"1","lastUpdated":"2026-06-09T00:00:00Z"},"status":"finished","class":{"system":"http://terminology.hl7.org/CodeSystem/v3-ActCode","code":"AMB"},"subject":{"reference":"Patient/patient-phase1"}}
                """,
            "DiagnosticReport" => """
                {"resourceType":"DiagnosticReport","id":"diagnosticreport-phase1","meta":{"versionId":"1","lastUpdated":"2026-06-09T00:00:00Z"},"status":"final","code":{"text":"Sample report"},"subject":{"reference":"Patient/patient-phase1"}}
                """,
            "Procedure" => """
                {"resourceType":"Procedure","id":"procedure-phase1","meta":{"versionId":"1","lastUpdated":"2026-06-09T00:00:00Z"},"status":"completed","subject":{"reference":"Patient/patient-phase1"}}
                """,
            "Immunization" => """
                {"resourceType":"Immunization","id":"immunization-phase1","meta":{"versionId":"1","lastUpdated":"2026-06-09T00:00:00Z"},"status":"completed","patient":{"reference":"Patient/patient-phase1"},"occurrenceDateTime":"2026-06-09"}
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(resourceType), resourceType, "No sample FHIR resource is available.")
        };
    }
}
