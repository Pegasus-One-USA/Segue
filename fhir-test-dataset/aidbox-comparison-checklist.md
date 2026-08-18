# Aidbox / Multi-Server Comparison Checklist

Use this to run the same dataset against **HAPI**, **Aidbox**, and **Medplum** and record how each
behaves. Check each box per server.

## Pre-flight

- [ ] Confirm `fhirVersion` is `4.0.1` (`GET /fhir/metadata?_summary=true`).
- [ ] Confirm transaction Bundles are supported (`rest[0].interaction` includes `transaction`).
- [ ] Confirm auth mode (segue HAPI = anonymous; Aidbox/Medplum typically require a token — set `FHIR_BEARER_TOKEN`).

## Upload

- [ ] **HAPI/Aidbox:** POST `bundles/hapi-aidbox/foundation-bundle.json`, then each `patient-00X-bundle.json`. Expect `200` with an entry per resource, `PUT` responses `201`/`200`.
- [ ] **Medplum:** POST `bundles/medplum/medplum-all-bundle.json`. Expect server-assigned ids in the response `entry[].response.location`.
- [ ] Re-run the same upload. Expect **no duplicates** (HAPI/Aidbox via PUT id; Medplum via `ifNoneExist`).

## Reference behavior

- [ ] Relative references resolve (`Observation?_include=Observation:patient`).
- [ ] Absolute reference on `encounter-001.subject` is accepted/resolved.
- [ ] Identifier-based reference on `coverage-003.payor` is accepted (logical reference).
- [ ] Nested backbone references resolve (`Procedure.performer.actor`, `CareTeam.participant.member`).

## Negative set (the RI contrast)

- [ ] **HAPI:** upload `negative/negative-bundle.json` → expect **rejection** (HAPI-1094, missing targets).
- [ ] **Aidbox:** same → expect **rejection** (RI enforced).
- [ ] **Medplum:** same → expect **acceptance** (RI not enforced; dangling refs stored).

## Search / query

- [ ] `Patient?identifier=urn:segue:test:patient|PATIENT-001` returns 1.
- [ ] `Observation?patient=Patient/patient-001` returns the patient's observations.
- [ ] `_revinclude` (`Patient?_id=patient-001&_revinclude=Observation:subject`) pulls observations.
- [ ] `_include` (`DiagnosticReport?_include=DiagnosticReport:result`) pulls observations.

## Comparison Matrix

Expected behavior per server. "client id" = client-assigned logical id like `patient-001`.
Fill/confirm the last column as you test.

| Capability | HAPI 8.10.0 | Aidbox | Medplum |
|---|---|---|---|
| Resource creation (client-assigned id) | ✅ accepts alphanumeric ids; ❌ rejects purely-numeric (HAPI-0960) | ✅ accepts client ids | ❌ rejects client logical ids — server assigns id |
| Resource creation (server-assigned id, POST) | ✅ | ✅ | ✅ (the required path) |
| Retrieval by id | ✅ `GET Type/id` | ✅ | ✅ (by server-assigned id) |
| Reference **storage** | literal, as written | literal, as written | literal, as written (no rewrite) |
| Reference **resolution** (`_include`) | ✅ | ✅ | ✅ |
| **Missing references** on write | ❌ **rejected** (RI, HAPI-1094) | ❌ **rejected** (RI on) | ✅ **accepted** (RI not enforced) |
| Absolute references | ✅ stored/resolved when base matches | ✅ | ✅ stored (may treat as external) |
| Relative references | ✅ | ✅ | ✅ |
| Identifier (logical) references | ✅ stored; resolution support varies | ✅ | ✅ |
| Search (`identifier`, `patient`, `code`) | ✅ | ✅ | ✅ |
| `_include` | ✅ | ✅ | ✅ |
| `_revinclude` | ✅ | ✅ | ✅ |
| Transaction Bundle | ✅ | ✅ | ✅ (primary upload path) |
| Conditional create (`ifNoneExist`) | ✅ | ✅ | ✅ (used to de-dupe by identifier) |
| Conditional update / delete | ✅ | ✅ | ✅ |
| Delete (referenced resource) | ❌ blocked while referenced (RI) | ❌ blocked (RI) | ✅ allowed (no RI) |
| Update (optimistic concurrency, ETag/`If-Match`) | ✅ | ✅ | ✅ |
| Validation on write | structural; profile validation opt-in | structural + optional profiles | structural |
| Terminology validation | not enforced by default | optional (terminology module) | limited by default |
| Version handling / history | ✅ `_history`, versioned reads | ✅ | ✅ |
| Error response shape | `OperationOutcome` | `OperationOutcome` (Aidbox format available) | `OperationOutcome` |

## Key differences to watch

1. **Ids:** Medplum forces server-assigned ids — that is the whole reason for `bundles/medplum/`
   (`urn:uuid` + `ifNoneExist`). HAPI/Aidbox happily take our `patient-001` style ids via PUT.
2. **Referential integrity:** the negative set is the litmus test. HAPI and Aidbox reject dangling
   references; Medplum stores them. If your pipeline relies on RI, do not rely on Medplum for it.
3. **Numeric ids:** never use purely-numeric client ids on HAPI (HAPI-0960). This dataset uses
   alphanumeric ids everywhere, so it is safe.
