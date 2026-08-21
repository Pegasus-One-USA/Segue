# HAPI FHIR Testing Guide (segue)

Target: `https://segue.pegasusone.com:7011/fhir` — HAPI FHIR 8.10.0, R4 4.0.1, anonymous.

All examples use `curl`. `-k` skips TLS verification (drop it if the cert chain is trusted).
Header `Content-Type: application/fhir+json` is used for writes; `Accept: application/fhir+json`
for reads. These commands are **run by you** — nothing in this repo executes them.

> Upload order: run the **transaction upload** section first (foundation bundle, then per-patient),
> or use `--mode individual` in the uploader, so that referenced resources exist before you query.

## 0. Capability statement (sanity)

```bash
curl -sk "https://segue.pegasusone.com:7011/fhir/metadata?_summary=true"
```

## 1. Transaction upload (recommended first step)

Load shared/foundation resources, then each patient graph. Bundles use `PUT` with deterministic
ids, so re-running does not create duplicates.

```bash
BASE=https://segue.pegasusone.com:7011/fhir

# Foundation first (Organizations, Locations, Practitioners, PractitionerRoles, Devices, Medications)
curl -sk -X POST "$BASE" \
  -H "Content-Type: application/fhir+json" \
  --data-binary @bundles/hapi-aidbox/foundation-bundle.json

# Then each patient
for p in 001 002 003 004 005; do
  curl -sk -X POST "$BASE" \
    -H "Content-Type: application/fhir+json" \
    --data-binary @bundles/hapi-aidbox/patient-$p-bundle.json
done
```

## 2. CRUD on a single resource

```bash
BASE=https://segue.pegasusone.com:7011/fhir

# CREATE / UPSERT with a client-assigned id (idempotent)
curl -sk -X PUT "$BASE/Patient/patient-001" \
  -H "Content-Type: application/fhir+json" \
  --data-binary @resources/Patient/patient-001.json

# READ
curl -sk "$BASE/Patient/patient-001"

# READ a specific version / history
curl -sk "$BASE/Patient/patient-001/_history"

# PATCH (JSON Patch) - change gender
curl -sk -X PATCH "$BASE/Patient/patient-001" \
  -H "Content-Type: application/json-patch+json" \
  -d '[{"op":"replace","path":"/gender","value":"male"}]'

# DELETE (blocked by RI if the resource is still referenced - expected)
curl -sk -X DELETE "$BASE/Patient/patient-001"
```

## 3. Searches

```bash
BASE=https://segue.pegasusone.com:7011/fhir

# By business identifier
curl -sk "$BASE/Patient?identifier=urn:segue:test:patient|PATIENT-001"

# All observations for a patient
curl -sk "$BASE/Observation?patient=Patient/patient-001"

# All conditions for a patient
curl -sk "$BASE/Condition?patient=Patient/patient-001"

# All encounters for a patient
curl -sk "$BASE/Encounter?patient=Patient/patient-001"

# Observations by LOINC code (HbA1c)
curl -sk "$BASE/Observation?code=http://loinc.org|4548-4"

# MedicationRequests for a patient
curl -sk "$BASE/MedicationRequest?subject=Patient/patient-001"
```

## 4. `_include` (pull referenced resources in)

```bash
BASE=https://segue.pegasusone.com:7011/fhir

# Observations plus the Patient each points to
curl -sk "$BASE/Observation?_include=Observation:patient"

# DiagnosticReports plus their result Observations and subject Patient
curl -sk "$BASE/DiagnosticReport?_id=diagnosticreport-001&_include=DiagnosticReport:result&_include=DiagnosticReport:subject"

# MedicationRequest plus the referenced Medication
curl -sk "$BASE/MedicationRequest?_id=medicationrequest-001&_include=MedicationRequest:medication"
```

## 5. `_revinclude` (pull resources that reference this one)

```bash
BASE=https://segue.pegasusone.com:7011/fhir

# patient-001 plus every Observation whose subject is that patient
curl -sk "$BASE/Patient?_id=patient-001&_revinclude=Observation:subject"

# patient-001 plus Conditions, Observations and Encounters that reference it
curl -sk "$BASE/Patient?_id=patient-001&_revinclude=Condition:subject&_revinclude=Observation:subject&_revinclude=Encounter:subject"
```

## 6. Referential-integrity demonstration (negative set)

```bash
BASE=https://segue.pegasusone.com:7011/fhir

# EXPECTED TO FAIL on HAPI (HAPI-1094): references a non-existent Patient
curl -sk -X PUT "$BASE/Observation/neg-observation-001" \
  -H "Content-Type: application/fhir+json" \
  --data-binary @negative/neg-observation-001.json
# -> HTTP 4xx OperationOutcome: resource Patient/patient-999-missing not found

# Whole negative transaction (EXPECTED TO FAIL on HAPI/Aidbox, would SUCCEED on Medplum)
curl -sk -X POST "$BASE" \
  -H "Content-Type: application/fhir+json" \
  --data-binary @negative/negative-bundle.json
```

## 7. Cleanup (reverse dependency order)

Delete leaf resources before the resources they reference. See `dependency-order.md`.
