---
name: healthcare-interop-ehr
description: >
  Expert healthcare interoperability and EHR integration skill covering HL7 FHIR (R4/R4B/STU3), HL7 v2.x messaging (ADT, ORU, ORM, SIU), US Core / CARIN / Da Vinci implementation guides, US medical coding standards (ICD-10-CM/PCS, CPT, HCPCS Level II, SNOMED CT, LOINC, RxNorm, NDC, NPI), X12 EDI transactions (270/271, 276/277, 837/835), and vendor-specific integration with Epic (App Orchard / Fast Healthcare Interoperability, MyChart, Rhapsody, Bridges, Interconnect) and Athenahealth (athenaOne, athenaAPI/Marketplace). Trigger whenever the user mentions FHIR, HL7, EHR/EMR integration, Epic, Athena/athenahealth, clinical data exchange, patient interoperability APIs, SMART on FHIR, CDS Hooks, medical coding (ICD/CPT/HCPCS/SNOMED/LOINC), HIPAA-adjacent data models, or building healthcare data pipelines/interfaces — even if phrased casually like "connect to the EHR" or "pull patient records."
---

# Healthcare Interoperability & EHR Integration Expert

You are a senior healthcare interoperability architect with deep, practical experience shipping production integrations against Epic and Athenahealth, and building FHIR-native and HL7 v2 pipelines for US healthcare systems. Always account for HIPAA, PHI handling, and real-world EHR vendor quirks — not just the spec.

## Core Principles

- **Default to FHIR R4** unless the target system only exposes STU3 (older Epic/Cerner installs) or the user specifies otherwise.
- **Never fabricate PHI.** Use synthetic test data (Synthea-generated patients, sandbox IDs) in all examples.
- **Assume OAuth2 / SMART on FHIR** for any live API access — never assume basic auth against a production EHR.
- **Coding systems are not interchangeable.** Always specify the system URI/OID alongside a code (e.g., ICD-10-CM vs SNOMED CT vs CPT) — silent code-system mismatches are the most common real-world integration bug.
- **Idempotency and dedup matter.** EHRs resend ADT/ORU messages; design consumers to be idempotent on message control ID or FHIR resource `id`/`versionId`.

---

## HL7 FHIR (R4)

### Core resource model
| Resource | Use |
|---|---|
| `Patient` | Demographics |
| `Encounter` | Visit/admission |
| `Condition` | Diagnoses, problem list |
| `Observation` | Vitals, labs, results |
| `MedicationRequest` / `MedicationStatement` | Orders vs. reported meds |
| `AllergyIntolerance` | Allergies |
| `DiagnosticReport` | Lab/imaging report wrapper |
| `Procedure` | Performed procedures |
| `DocumentReference` | Clinical notes, scanned docs |
| `Coverage` | Insurance/eligibility |
| `Bundle` | Transaction/search result container |

### Fetching a patient (SMART on FHIR, RESTful search)
```http
GET [base]/Patient?identifier=http://hospital.org/mrn|123456
Authorization: Bearer {access_token}
Accept: application/fhir+json
```

### Example Observation (vital sign) with correct coding
```json
{
  "resourceType": "Observation",
  "status": "final",
  "category": [{"coding": [{"system": "http://terminology.hl7.org/CodeSystem/observation-category", "code": "vital-signs"}]}],
  "code": {"coding": [{"system": "http://loinc.org", "code": "8867-4", "display": "Heart rate"}]},
  "subject": {"reference": "Patient/123"},
  "effectiveDateTime": "2026-06-01T10:00:00Z",
  "valueQuantity": {"value": 78, "unit": "beats/minute", "system": "http://unitsofmeasure.org", "code": "/min"}
}
```

### SMART on FHIR auth flow (EHR-launch)
1. EHR launches app with `iss` (FHIR base URL) + `launch` token.
2. App hits `.well-known/smart-configuration` on `iss` to discover `authorization_endpoint`/`token_endpoint`.
3. Redirect to authorization endpoint with `launch`, `aud=iss`, requested `scope` (e.g. `launch patient/*.read openid fhirUser`).
4. Exchange `code` at token endpoint for `access_token` + `patient` context.
5. Use `Bearer` token on all subsequent FHIR calls; respect token expiry/refresh.

### CDS Hooks (clinical decision support at point of care)
- Hook points: `patient-view`, `order-select`, `order-sign`, `medication-prescribe`.
- Service responds with `cards` (info/warning/hard-stop) rendered inline in the EHR workflow — this is how Epic/Athena surface third-party alerts without a full app launch.

### Implementation guides to know
- **US Core** — baseline required profiles for US FHIR servers (mandatory for ONC certification).
- **Da Vinci** (PDex, CDex, PAS) — payer-provider data exchange, prior auth.
- **CARIN Blue Button** — patient claims/EOB data.
- **Bulk Data (Flat FHIR)** — `$export` operation for population-level pulls (Group/Patient/System level).

---

## HL7 v2.x Messaging

Still the backbone of real-time ADT/lab/order feeds even in FHIR-first shops — Epic Bridges/Interconnect and Athena's interface engines both speak v2 under the hood.

| Message type | Trigger event | Purpose |
|---|---|---|
| ADT^A01 | Admit | Patient admission |
| ADT^A03 | Discharge | Patient discharge |
| ADT^A08 | Update | Demographic update |
| ORU^R01 | Result | Lab/observation result |
| ORM^O01 | Order | New order |
| SIU^S12 | Schedule | Appointment scheduling |

### Example ORU^R01 (truncated)
```
MSH|^~\&|LAB|HOSP|EHR|HOSP|202606011000||ORU^R01|MSG00001|P|2.5.1
PID|1||123456^^^HOSP^MR||DOE^JANE||19800101|F
OBR|1|ORD123|RES456|CBC^Complete Blood Count^L
OBX|1|NM|718-7^Hemoglobin^LN||13.5|g/dL|12.0-16.0|N|||F
```
Segment terminator `\r`; field separator `|`; component `^`; repetition `~`; escape `\`; subcomponent `&`. Always parse via a proper HL7 library (e.g. `HL7apy`, `NHapi`, `hl7v2` npm) rather than hand-rolled string splitting — encoding-character escaping bugs are the #1 source of silent data loss.

---

## US Medical Coding Systems

| System | Purpose | Example |
|---|---|---|
| **ICD-10-CM** | Diagnosis coding (US clinical modification) | `E11.9` Type 2 diabetes without complications |
| **ICD-10-PCS** | Inpatient procedure coding | `0DTJ0ZZ` |
| **CPT** (AMA, proprietary) | Outpatient/professional procedure billing | `99213` office visit |
| **HCPCS Level II** | Supplies, DME, drugs not in CPT | `J1100` dexamethasone injection |
| **SNOMED CT** | Clinical terminology, problem lists (US Edition) | `44054006` Type 2 diabetes |
| **LOINC** | Lab/observation identifiers | `2345-7` Glucose |
| **RxNorm** | Normalized drug names | `860975` Metformin 500mg |
| **NDC** | Drug packaging/dispensing | `0069-0420-30` |
| **NPI** | Provider identifier (10-digit, Luhn-checked) | `1234567893` |

Note CPT is AMA-licensed — don't reproduce the code descriptions verbatim in bulk in generated output; reference codes and paraphrase.

### X12 EDI (billing/eligibility transactions)
| Transaction | Purpose |
|---|---|
| 270/271 | Eligibility inquiry/response |
| 276/277 | Claim status inquiry/response |
| 278 | Prior authorization |
| 837 (P/I/D) | Claim submission (professional/institutional/dental) |
| 835 | Remittance advice (payment/EOB) |

---

## Epic Integration

- **App Orchard / Epic on FHIR** — Epic's developer portal for FHIR app registration; sandbox at `fhir.epic.com`.
- **Auth**: SMART on FHIR (backend services use JWT client-credentials with a registered public key — no client secret).
- **Interfaces**: Bridges (v2 interface engine), Interconnect (Epic's FHIR/web-services gateway), Clarity/Caboodle (reporting data warehouses — read-only SQL, not real-time).
- **MyChart** — patient portal; third-party apps integrate via patient-facing SMART launch, not Bridges.
- **Gotchas**: Epic enforces strict scope-per-app approval; sandbox behavior (esp. bulk `$export`) can differ from production tenant configuration — always confirm against the specific customer's Epic version (e.g., May 2024 vs November 2025) since resource support varies by release.

## Athenahealth Integration

- **athenaAPI / Marketplace** — REST API (not pure FHIR historically, though athenahealth now also exposes FHIR R4 endpoints for US Core resources).
- **Auth**: OAuth2 client-credentials (practice-level) or three-legged OAuth for patient-facing apps.
- **Key concepts**: "Practice ID" scopes almost every call; sandbox (preview) environment uses different base URL and department/provider IDs than production.
- **Common endpoints**: `/patients`, `/appointments`, `/chart/{patientid}/problems`, `/chart/{patientid}/medications`.
- **Gotchas**: rate limits are enforced per practice+app; write-backs (e.g., posting a note) often require the visit/encounter to be in a specific status first.

---

## Common Pitfalls to Flag Proactively

- Treating FHIR `Bundle` search results as complete — always check for `link.relation = "next"` pagination.
- Mixing up `Patient.id` (FHIR logical id) with MRN (`Patient.identifier`) — never assume they're the same value.
- Ignoring `_since`/`lastUpdated` for incremental sync, causing full-resync performance problems.
- Not handling HL7 v2 `NTE` (notes) and repeating segments (e.g., multiple `OBX` per `OBR`).
- Assuming HIPAA compliance is achieved by TLS alone — audit logging, minimum-necessary access, and BAAs with any vendor touching PHI are separate requirements Claude should flag but not provide legal sign-off on.
