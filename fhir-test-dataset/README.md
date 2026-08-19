# FHIR R4 (4.0.1) Interconnected Test Dataset

A complete, cross-referenced FHIR **R4 / 4.0.1** patient graph plus tooling, generated
deterministically by `generate.js`. Built and verified against the target server:

- **HAPI FHIR 8.10.0**, FHIR R4 4.0.1, base `https://segue.pegasusone.com:7011/fhir`
- Anonymous (no auth), transaction Bundles supported, referential integrity enforced.

> This folder is **generator output only**. Nothing here is uploaded automatically.
> Use the C# uploader (or `curl`) yourself when you want data on a server.

## Regenerate

```bash
node generate.js
```

Rebuilds every file below from scratch (deterministic — re-running produces byte-identical output).

## Folder map

| Path | What it is |
|---|---|
| `generate.js` | The single generator. Builds the whole graph in memory, wires all references, emits every file. |
| `resources/<ResourceType>/<id>.json` | One file per resource (69 valid resources). |
| `bundles/hapi-aidbox/` | Transaction Bundles, **PUT + deterministic ids** (HAPI & Aidbox). `foundation-bundle.json` first, then `patient-00X-bundle.json`, plus `admission-bundle.json`. |
| `bundles/medplum/` | Transaction Bundles, **POST + `urn:uuid` + `ifNoneExist`** (Medplum). `medplum-all-bundle.json` (whole graph, recommended) + `foundation` + self-contained per-patient bundles. |
| `negative/` | Intentionally broken-reference resources + `negative-bundle.json`. Negative test cases. |
| `manifest.json` | Machine-readable index: every resource, its file, references, dependencies, `testCategory`. |
| `validation-report.md` | Reference-validation results (generated from the emitted files — reflects reality). |
| `dependency-order.md` | Recommended creation order + note on transaction resolution. |
| `reference-styles.md` | The five reference-style test cases and where each lives. |
| `hapi-testing.md` | Concrete `curl` CRUD / search / `_include` / `_revinclude` / transaction examples. |
| `aidbox-comparison-checklist.md` | Checklist + HAPI vs Aidbox vs Medplum comparison matrix. |
| `expected-scenarios.md` | Expected test scenarios and per-server expected outcomes. |
| `uploader/` | .NET 9 console uploader (compiles; does not auto-run). |

## Dataset at a glance

5 Patient, 3 Practitioner, 2 PractitionerRole, 2 Organization, 2 Location, 3 Encounter,
5 Condition, 10 Observation, 3 DiagnosticReport, 3 Procedure, 3 MedicationRequest,
3 Medication, 3 AllergyIntolerance, 3 CarePlan, 3 ServiceRequest, 3 Immunization,
3 DocumentReference, 3 CareTeam, 3 Coverage, 2 RelatedPerson, 2 Device — **69 resources**,
plus **5 negative** resources.

Five distinct clinical scenarios: `patient-001` Type 2 Diabetes, `patient-002` Hypertension,
`patient-003` Respiratory (COPD), `patient-004` Acute hospitalization (pneumonia),
`patient-005` Preventive care / immunizations. See `expected-scenarios.md`.

Terminology: SNOMED CT, LOINC, RxNorm, ICD-10-CM, CVX, CPT — genuine common codes, **not**
terminology-server-validated (dependency noted in `validation-report.md`).

## HAPI vs Aidbox vs Medplum

The same logical graph is emitted in **two upload flavors** because the servers disagree on two things:
client-assigned ids, and referential integrity.

| | Client-assigned logical ids | Referential integrity on write | Use this bundle set |
|---|---|---|---|
| **HAPI 8.10.0** | Accepts **alphanumeric** ids (`patient-001`). Rejects purely-numeric ids (HAPI-0960). | **Enforced** — a reference to a non-existent resource is rejected (HAPI-1094). | `bundles/hapi-aidbox/` |
| **Aidbox** | Accepts client-assigned ids. | **Enforced** (RI on). | `bundles/hapi-aidbox/` |
| **Medplum** | **Rejects** client-assigned logical ids — the server assigns ids. | **Not enforced** — dangling references are stored as-is. | `bundles/medplum/` |

**Why two flavors:**

- `bundles/hapi-aidbox/` uses `PUT Type/id` with our deterministic alphanumeric ids and
  literal relative references (`Patient/patient-001`). PUT makes re-runs idempotent (no
  duplicates). Because HAPI/Aidbox enforce RI, **load `foundation-bundle.json` first**, then
  the per-patient bundles (or use a single transaction — see below).
- `bundles/medplum/` uses `POST` with `fullUrl: urn:uuid:…`, references rewritten to those
  `urn:uuid` values (resolved intra-bundle), and `request.ifNoneExist` conditional-create by
  business `identifier` so re-running does not duplicate. The server assigns real ids.
  `medplum-all-bundle.json` contains the entire graph in one transaction (recommended, since
  `urn:uuid` refs only resolve **within** a single bundle); the per-patient Medplum bundles are
  made self-contained (they include the foundation resources they need) so they resolve too.

**Transaction resolution note:** inside a single transaction Bundle, references resolve
regardless of entry order — the server orders the operations. Dependency order only matters
when you upload resources/bundles *separately* (e.g. individual mode, or foundation-then-patients).

## Using the uploader

See `uploader/` and `hapi-testing.md`. The uploader supports:

```bash
cd uploader
dotnet run -- --mode validation                 # local reference check, no network
dotnet run -- --mode dry-run --flavor hapi       # print the plan, no calls
dotnet run -- --mode individual                  # PUT files in dependency order (idempotent, resumable)
dotnet run -- --mode transaction --flavor hapi   # POST the hapi-aidbox transaction bundles
dotnet run -- --mode transaction --flavor medplum
```

Auth is anonymous by default. To target a secured server, set `FHIR_BEARER_TOKEN` in the
environment (preferred) — credentials are never hardcoded.
