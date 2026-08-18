# Expected Test Scenarios and Outcomes

Five distinct clinical scenarios plus reference-style and negative scenarios. For each, the
expected outcome per server is listed. "Success" for an upload means HTTP `200` transaction
response with a positive `entry[].response.status` per resource.

## Clinical scenarios (valid data)

### Scenario A — `patient-001`: Type 2 Diabetes
- **Graph:** Patient → Encounter (`encounter-001`, ambulatory) → Practitioner (`practitioner-001`).
  Conditions: T2DM (`condition-001`) + comorbid hypertension (`condition-005`).
  Observations: glucose (`observation-001`), HbA1c (`observation-002`), blood-pressure panel with
  systolic/diastolic components (`observation-003`) → DiagnosticReport (`diagnosticreport-001`,
  `result` = glucose + HbA1c) → MedicationRequest metformin (`medicationrequest-001` →
  `medication-001`) → CarePlan (`careplan-001`, addresses `condition-001`). Plus Procedure ECG,
  ServiceRequest HbA1c, CareTeam, Coverage, RelatedPerson (spouse), DocumentReference (progress
  note), Device (glucose meter).
- **Reference-style content:** `encounter-001.subject` is an **absolute** reference.
- **Expected:** all servers store the graph. HAPI/Aidbox require foundation + patient bundle
  order (or single transaction). Metformin RxNorm `860975`, HbA1c LOINC `4548-4`, T2DM SNOMED
  `44054006` / ICD-10 `E11.9`.

### Scenario B — `patient-002`: Hypertension
- **Graph:** Patient (no encounter) → Condition essential hypertension (`condition-002`) →
  Observations blood pressure (`observation-004`, elevated 158/96) + heart rate (`observation-005`)
  → MedicationRequest lisinopril (`medicationrequest-002` → `medication-002`) → CarePlan
  (`careplan-002`) → Coverage (`coverage-002`).
- **Expected:** all servers store it. Demonstrates clinical resources that legitimately omit
  `encounter` (optional in R4).

### Scenario C — `patient-003`: Respiratory (COPD)
- **Graph:** Patient → Encounter (`encounter-002`) → Practitioner (`practitioner-003`, pulmonology).
  Condition COPD (`condition-003`, SNOMED `13645005` / ICD-10 `J44.9`). Observations O2 sat
  (`observation-006`, 91%) + respiratory rate (`observation-007`) → DiagnosticReport chest X-ray
  (`diagnosticreport-002`). Procedure spirometry (`procedure-002`), ServiceRequest chest X-ray,
  CarePlan COPD, AllergyIntolerance peanut, CareTeam, Device (pulse oximeter).
- **Expected:** all servers store it.

### Scenario D — `patient-004`: Acute hospitalization (pneumonia)
- **Graph:** Patient → Encounter (`encounter-003`, **inpatient**, ward `location-002`) →
  Practitioner (`practitioner-002`). Condition pneumonia (`condition-004`). Observations temperature
  (`observation-008`, 38.7 °C) + heart rate (`observation-009`, 104) → DiagnosticReport CBC
  (`diagnosticreport-003`). Procedure chest X-ray (`procedure-001`), ServiceRequest blood culture,
  MedicationRequest amoxicillin (`medicationrequest-003` → `medication-003`), AllergyIntolerance
  sulfonamide, CareTeam, DocumentReference discharge summary, RelatedPerson (daughter), Coverage
  (`coverage-003`).
- **Reference-style content:** `coverage-003.payor` is an **identifier-based** reference to the
  payer Organization. Also emitted as the self-contained `admission-bundle.json`.
- **Expected:** all servers store it.

### Scenario E — `patient-005`: Preventive care / immunizations
- **Graph:** Patient → Observation BMI (`observation-010`) + 3 Immunizations: influenza CVX `140`
  (`immunization-001`), Tdap CVX `115` (`immunization-002`), pneumococcal PCV13 CVX `133`
  (`immunization-003`) + DocumentReference summary.
- **Expected:** all servers store it.

## Reference-style scenario (valid)
- Relative (default everywhere), absolute (`encounter-001.subject`), identifier/logical
  (`coverage-003.payor`), nested-backbone (participant/location/performer/member/result),
  cross-type (`diagnosticreport-001`).
- **Expected:** all styles accepted by HAPI/Aidbox/Medplum. Absolute reference is non-portable
  (keeps the Segue base URL) — a known, intentional caveat.

## Negative scenarios (broken references) — `negative/`

| Resource | Broken reference | HAPI | Aidbox | Medplum |
|---|---|---|---|---|
| `neg-observation-001` | subject → `Patient/patient-999-missing` | ❌ reject | ❌ reject | ✅ accept |
| `neg-condition-001` | encounter → `Encounter/encounter-999-missing` | ❌ reject | ❌ reject | ✅ accept |
| `neg-diagnosticreport-001` | result → `Observation/observation-999-missing` | ❌ reject | ❌ reject | ✅ accept |
| `neg-medicationrequest-001` | medicationReference → `Medication/medication-999-missing` | ❌ reject | ❌ reject | ✅ accept |
| `neg-encounter-001` | participant.individual → `Practitioner/practitioner-999-missing` | ❌ reject | ❌ reject | ✅ accept |
| `negative-bundle.json` (all 5) | mixed | ❌ transaction fails | ❌ transaction fails | ✅ transaction succeeds |

**The contrast is the test:** the negative set proves whether a server enforces referential
integrity. HAPI (HAPI-1094) and Aidbox reject; Medplum accepts and stores the dangling references.

## Idempotency / re-run expectations
- **HAPI/Aidbox** (`bundles/hapi-aidbox/`, PUT + deterministic ids): re-running updates in place,
  **no duplicates**.
- **Medplum** (`bundles/medplum/`, POST + `ifNoneExist=identifier=…`): re-running matches on the
  business identifier and does **not** create duplicates.
- **Uploader `--mode individual`:** records created ids in `upload-state.json`; a resumed run
  **skips** already-created resources.

## Terminology caveat
SNOMED CT, LOINC, RxNorm, ICD-10-CM, CVX, CPT codes are genuine and syntactically valid but are
**not** validated against a terminology server. Membership/validity checks are out of scope for
these scenarios (structural + reference correctness only).
