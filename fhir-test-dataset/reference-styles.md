# Reference-Style Test Cases

FHIR R4 references can be expressed several ways. This dataset deliberately includes an example
of each so a client/server can be exercised against all of them. Each is labelled in
`manifest.json` (see `testCategory` and each reference's `style`).

| # | Style | Where in this dataset | Example (from the file) |
|---|---|---|---|
| 1 | **Relative reference** | The default across the whole dataset | `Observation/observation-001` → `"reference": "Patient/patient-001"` |
| 2 | **Absolute reference** | `Encounter/encounter-001` `subject` (marked `testCategory: reference-style`) | `"reference": "https://segue.pegasusone.com:7011/fhir/Patient/patient-001"` |
| 3 | **Identifier-based / logical reference** (`Reference.identifier`, no `.reference`) | `Coverage/coverage-003` `payor` (marked `testCategory: reference-style`) | `"payor": [{ "identifier": { "system": "urn:segue:test:organization", "value": "ORG-002" }, "display": "..." }]` |
| 4 | **Reference nested in a backbone element** | `Encounter.participant.individual`, `Encounter.location.location`, `Procedure.performer.actor`, `CareTeam.participant.member`, `DiagnosticReport.result[]` | `encounter-001` → `participant[0].individual.reference = "Practitioner/practitioner-001"` |
| 5 | **References across different resource types** | Throughout; concentrated in `DiagnosticReport/diagnosticreport-001` | one resource references Patient, Encounter, Organization and Observation(s) at once |

## Detail

### 1. Relative reference (`Type/id`)
The portable default. Resolves against the server base. Used by ~130 references here.
Recommended for cross-server portability.

### 2. Absolute reference (full URL)
`encounter-001.subject` points at
`https://segue.pegasusone.com:7011/fhir/Patient/patient-001`. HAPI stores and resolves absolute
references whose base matches its own server base. **Caveat:** absolute references are *not*
portable — loading this resource into a different server keeps the hard-coded Segue URL, so the
reference points off-box. This is intentional, to test absolute-reference handling.

### 3. Identifier-based (logical) reference
`coverage-003.payor` carries `Reference.identifier` instead of `Reference.reference`. The target
is identified by its business identifier (`urn:segue:test:organization|ORG-002` = `org-002`)
rather than by a literal id. Servers that support logical references (and reference resolution by
identifier / `_include` on logical refs varies by server) can resolve it; RI enforcement generally
does **not** apply to logical references, so this entry is valid even where literal RI is enforced.
In `manifest.json` its `resolves` value is `"n/a (resolved by identifier)"`.

### 4. Reference nested in a backbone element
Not all references sit at the top level. This dataset exercises references inside backbone
elements: `Encounter.participant.individual`, `Encounter.location.location`,
`Procedure.performer.actor`, `CareTeam.participant.member`, and the `DiagnosticReport.result[]`
array. A client that only scans top-level fields will miss these.

### 5. References across different resource types
`diagnosticreport-001` alone references a Patient (`subject`), an Encounter (`encounter`), an
Organization (`performer`), and two Observations (`result[]`) — four distinct target types from one
resource. Good for testing `_include` with multiple target types.
