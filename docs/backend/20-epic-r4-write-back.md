# 20 — EHR write-back to Epic (FHIR R4)

> Part of the [Backend Architecture Guide](README.md).
> **Status: Phase 0 (discovery) complete, 2026-09-30 — desk research plus the Phase 0b sandbox tests in section 6.
> Phase 1 (framework, dry run only) built 2026-10-01 — see section 7. Phases 2–5 not started.**
> **Branch:** all phases are built on `feature/ehr-write-back`, one commit per phase, no per-phase pull requests.
> **Living plan:** the Claude Doc "Epic FHIR R4 Write-Back — Implementation Plan"
> (https://claude.ai/code/artifact/5cd4f8a3-f740-49b6-bd65-930953a45762) holds the full design, the workflow-builder
> behaviour and the decisions still open. This file records the verified Epic facts the implementation depends on.

Everything below comes from Epic's own published material, fetched 2026-09-30: the sandbox R4 CapabilityStatement
(software "Epic August 2026"), the sandbox SMART configuration, the interface catalog at open.epic.com, and each
API's raw specification from `https://fhir.epic.com/Specifications/Api?id=<id>`. Each API's facts were extracted
by one agent and re-checked against the spec by a second, adversarial one.

## 1. What Epic R4 accepts

Epic R4 accepts bridge-style writes for **five resource types**, plus `Patient/$match` for patient resolution.

| Epic API (spec id) | Audiences Epic lists | Encounter needed | Where the data lands | Decision |
|---|---|---|---|---|
| AllergyIntolerance.Create (Patient Chart) (945) | Backend, Clinician, Patient | No | Holding tank until a clinician reconciles it | **In** |
| Condition.Create (Problems) (949) | Backend, Clinician, Patient | No | Holding tank; a provider reviews and reconciles | **In** |
| DocumentReference.Create (Clinical Notes) (1046) | Backend, Clinician, Patient | Yes — an existing encounter of the same patient that can hold a note (the sandbox accepted a finished inpatient stay) | Chart: signed if `final`, draft if `preliminary` | **In** |
| Observation.Create (Vital Signs) (963) | Backend, Clinician, Patient | Yes for backend/clinician calls — existing, not closed, not a future appointment | Chart flowsheets | **In** |
| Patient.Create (Demographics) (930) | Backend, Clinician | n/a | A new patient, **or an existing one** when Epic finds a certain match | **In, opt-in only** |
| Patient.$match (10423) | Backend, Clinician, Patient (not MyChart) | n/a | Nothing filed; at most one certain match | **In** (resolution) |
| Observation.Create / .Update (Lines, Drains, Airways) (962 / 974) | Backend, Clinician, Patient | Yes, not closed | LDA flowsheet | Deferred — spec contradicts itself on required elements |
| DocumentReference.Create / .Update (Document Information) (10050 / 10051) | Backend, Clinician, Patient | Sometimes | Scan metadata | Out — Hyperdrive scan workflow only |
| DocumentReference.Create (Non-Patient Document Information) (10303) | Backend, Clinician, Patient | n/a | Tapestry non-patient documents | Out |
| QuestionnaireResponse.Create (Patient-Entered) (10023) | Backend, Clinician | n/a | Answers a questionnaire Epic already assigned | Out — needs Epic questionnaire/question ids and an existing assignment |
| Communication.Create (Community Resource) (10090) | Backend, Clinician | Yes | Community-referral message | Out — referral messaging only |
| Goal.Create (Patient) (878) | STU3 only | n/a | Received document | Out — no R4 create |

**How the list was enumerated.** Every R4 entry in Epic's interface catalog (639 interfaces) that is not a plain
Read or Search was listed. All other R4 write APIs belong to CDS Hooks (unsigned orders, encounter diagnoses),
Radiotherapy (BodyStructure, ServiceRequest, Procedure), DICOM (Observation), Prior Auth (ServiceRequest,
MedicationRequest, Claim) or Community Resource (Task) workflows, and are out. `DiagnosticReport` update and
`ConceptMap` create appear in the CapabilityStatement but have no public spec.

## 2. Platform facts that constrain the design

- **Nothing is undoable, nothing is conditional.** No R4 resource has `delete` or `patch`; there is no
  system-level interaction, so no batch or transaction. `conditionalCreate` and `conditionalUpdate` are false on
  every resource, so `If-None-Exist` cannot be used. AllergyIntolerance, Condition and Patient have no update.
  The write ledger is the only idempotency mechanism, and every write is one POST.
- **Holding tank.** AllergyIntolerance and Condition creates are not on the chart until a clinician reconciles
  them, and allergies are not searchable until then — search-before-create cannot find our own earlier writes.
- **Server-side duplicates — only vitals are protected.** Replaying an identical AllergyIntolerance, Condition or
  DocumentReference create in the sandbox filed a second record each time (new id, 201). Holding-tank items do not
  trigger 59141 "An attempt was made to create a duplicate record" (documented for an allergy already on the
  chart), and Condition's documented "duplicate checks" did not fire. A replayed vital fails with 400 59189 "Failed
  to file the reading", diagnostics "Reading already exists", expression `code/instant`. Map 59141 and 59189 to
  "already at target", never a retry. For the other three types the ledger is the only duplicate protection, and a
  write whose response was lost must stay `Unknown` for a person to check.
- **Fatal modifier values (Nov 2022 change).** An unaccepted value on a modifier element fails the whole request
  (sandbox: Condition `verificationStatus` confirmed → **422** 59012, diagnostics "Only a verificationStatus of
  'provisional' is supported.", expression `verificationstatus`).
  Accepted: Condition `verificationStatus` provisional, `clinicalStatus` active or resolved per abatement;
  Observation `status` final; Patient `active` true and `deceasedBoolean` false (`deceasedDateTime` and `link`
  are always fatal); AllergyIntolerance `clinicalStatus` active. AllergyIntolerance `verificationStatus`: the
  element text says other values "are ignored" while the change log says unaccepted values are fatal — send
  `unconfirmed` or omit it.
- **Encounters cannot be created.** Encounter is read/search only in R4. Notes (1046) and vitals (963) need an
  existing eligible encounter of the target patient; never reuse a source encounter id.
- **Patient.Create is match-or-create.** An identifier match that passes demographic validation, or a single
  high-confidence demographic match, returns the existing patient; the spec states no status that distinguishes
  this from a new create. Always call `$match` first and verify the returned id.
- **Patient.Create required fields are organization build.** The sandbox rejects a patient without an SSN:
  400 59108 "A required element is missing.", expression `identifier (ssn)`. Treat 59108 as a configuration error
  naming the element, and let each connection declare which identifiers its Epic build requires.
- **`$match`** (sandbox-verified).
  - `onlyCertainMatches` must be `valueBoolean: true`. The string `"true"` from Epic's sample fails (400 59102),
    and `false` is not supported (400 59138).
  - Outcomes: one certain match → 200, `search.score` 1, grade `certain`; 59013 low-confidence only → **400**,
    severity fatal; 4101 none → 200, severity warning; 59011 several high-confidence matches (not seen in the
    sandbox). Only 4101 may lead to an opt-in create; 59011 and 59013 go to manual review.
  - Name, birth date and gender alone gave 59013 for both sandbox patients; adding the patient's telecom and
    address gave a certain match. Epic's own identifier systems (MRN and internal OIDs, the FHIR-id systems) are
    ignored with information-level 59109 "…is not supported for this interaction", and more than one `official`
    name draws 59109 on `name.use`. Send one official name, phone and address; do not count on identifiers.
  - Not supported for organizations in the Netherlands.
- **Response** (sandbox-verified). Location is always relative (`Type/id`) — still parse an absolute URL too. No
  ETag. Without `Prefer` a create returns 201 with an empty body, the same as `return=minimal`;
  `return=representation` returns the filed resource. Always send `Prefer` explicitly.
- **Patient reference form.** Every sample uses a relative literal `Patient/<Epic FHIR id>`.
- **Codes and text.**
  - Condition: ICD-10 and SNOMED "supported and preferred"; other systems only with org-configured mappings.
    "Either code.coding.display or code.text must be populated" — the only API that documents using display text.
  - Patient extensions: "All children of the coding object aside from code is ignored."
  - Vitals: `code.coding` is 1..1; rows resolve by LOINC, encoded flowsheet id or an environment-specific internal
    OID (sandbox: LOINC 29463-7 alone resolved to the Weight row and came back with the flowsheet OID and id added).
    The spec says the category system must be `http://hl7.org/fhir/observation-category`, but the sandbox also
    accepted `http://terminology.hl7.org/CodeSystem/observation-category`.
  - Returned codes moved from OIDs to URLs in Nov 2022. The sandbox accepted the URL forms for RxNorm
    (allergen), ICD-10-CM (problem) and LOINC (vital, note type); OID input forms are untested.
  - Allergy: Epic replaced our `code.text` with the coding's display, and did not echo `category` or
    `recordedDate` in the returned resource.
- **Units.** Vitals: height, weight and temperature units are validated and converted; "For other readings or if
  a unit is not provided, a default unit is used … The default unit varies between the type of reading and
  between organizations." Blood-pressure units are ignored (mm[Hg]).
- **Lengths.** Condition note 450 characters (one note). Vitals note 254 characters (60 before May 2025).
  Clinical notes are `text/plain` base64 only, and "Epic only saves the first attachment".
- **Notes status.** `docStatus` `final` "corresponds to a value of signed"; `preliminary` leaves the note "so that
  a clinician can review or edit the document before signing it". Pre-charting (Feb 2025+) requires preliminary.
  With no `author` sent, Epic files the note under the background user (sandbox: Practitioner "User
  Interconnect") and adds the US Core `clinical-note` category.
- **Vital timing.** The sandbox filed a vital timed now against an in-progress visit whose period is in 2019, so the
  effective time is not checked against the encounter period, at least for an open encounter.

## 3. Write profile rules

Profile rules should be data-driven per Epic API id, not hard-coded. Rules still marked *(test)* were not settled
by the Phase 0b run and must stay switchable in configuration.

| Resource | Must send | Force / strip / rewrite | Skip the record when |
|---|---|---|---|
| AllergyIntolerance | `patient` as `Patient/<Epic id>`; exactly one `manifestation` per reaction | `clinicalStatus` active; `verificationStatus` unconfirmed or omitted; strip `id`; `category` and `recordedDate` are accepted but not echoed; `code.text` is replaced by the coding display | Inactive, entered-in-error, "no known allergies"; already in the ledger (Epic does not deduplicate) |
| Condition | `subject`; `code` with ≥ 1 coding carrying a code (ICD-10 / SNOMED preferred); `code.coding.display` or `code.text`; onset (sent in the test; whether it is required is *(test)*) | `verificationStatus` provisional (anything else → 422 59012); merge notes into one ≤ 450 chars | Inactive, entered-in-error, text-only (no code); already in the ledger (Epic does not deduplicate) |
| DocumentReference (clinical note) | `subject`; `context.encounter` of the same patient (finished encounters accepted); one attachment, `text/plain`, base64; `type` from Epic's LOINC list or the org's note-type OID; `status` current; explicit `docStatus` | Convert HTML/RTF to plain text; first attachment only; omit `author` (Epic files under the background user) | No encounter; Discharge / Patient Instructions types; scanned source note; already in the ledger (Epic does not deduplicate) |
| Observation (vital sign) | `subject`; `encounter` (eligible); category `vital-signs` (either category URL); one coding (LOINC is enough); `effectiveDateTime` with offset; value with unit; `status` final | Strip `id`; blood pressure as components 8480-6 / 8462-4; note ≤ 254 chars | No eligible encounter; unit not convertible to the org default; duplicable flowsheet row; same patient + code + time in the ledger. 59189 "Reading already exists" → already at target |
| Patient (opt-in) | One official name (family + 1–2 given); `birthDate`; `gender`; phone/email only; ≤ 1 home address with `city` and `line`; the identifiers the org build requires (sandbox: SSN) | `active` true; strip `deceasedDateTime`, `link`, `generalPractitioner`; clone mode strips every source identifier and must then supply any required identifier itself | `$match` 59011 / 59013 (manual review) or a certain match (use it) |

## 4. Authorization

- Epic grants whatever APIs are on the app's client ID. The backend token request needs only `grant_type`,
  `client_assertion_type` and `client_assertion`; `scope` is not a documented backend parameter. Sandbox-verified:
  every requested `scope` (none, v2 `.c`, v1 `.write`, `system/*.read`, an unregistered type) returned 200 with the
  same granted set, and that set used v1 spelling (`system/Observation.write`, `system/Patient.read`) although the
  app was registered for SMART v2. Send no scope to Epic, and read the granted `scope` from the token response.
- A call to an API that is not on the app returns **403 with an empty body** (sandbox: reading back our own
  AllergyIntolerance, Condition, Observation and DocumentReference, because only the create APIs were registered).
- Backend writes are audited under a background user the customer's Epic security team maps to the client ID
  (missing mapping: 400 `unauthorized_client`). The user also needs security points per API (listed only in
  Galaxy). A missing API: 403 `insufficient_scope`.
- Nothing about an app can change after "Ready for Production", so **write-back uses its own client ID**
  (Backend Systems, JWK Set URL, the write APIs + `Patient.$match` + the reads the writer needs such as Encounter
  search).
- JWK Set URL: the sandbox has not accepted static keys since February 2026; from the May 2026 Epic version all
  backend apps must use a JKU (customers may configure local JWKs from `.pem` files from August 2026). Client
  assertion `exp` at most 5 minutes out; `jti` at most 151 characters, never reused.
- Write scope spelling, when a scope is sent: SMART v1 `system/<Type>.write`, or v2 `system/<Type>.c` (create) /
  `.u` (update), matching the app's SMART scope version. Epic's docs disagree on whether v2 arrived in Aug or Nov
  2024.
- Sandbox: `https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4/`, background user auto-mapped, written
  data wiped every Sunday at about 8 PM CT.

## 5. What the current code does, and what Phase 1 must change

- `SmartBackendServicesTokenProvider.ResolveScopeString` sends the connection's scopes, falling back to
  `system/*.read` for Epic when none are configured. `SourceConnectionRuntimeResolver` regenerates Epic scopes
  as `system/<Type>.rs` (v2) from the retrieval resource types.
- `ScopeGeneratorService` refuses write levels on purpose (`ReadAccessLevelPreference = ["read", "rs", "r"]`).
  Keep that for reads; add a separate write-purpose generator (a strategy, not a switch).
- `EpicSourceConnectionScopeSyncService` leaves Epic Backend connections' scopes untouched.
- `BackendAuthScopeProbeService` (Test Connection) defaults to `system/*.read`.
- `FhirDestinationOAuth2TokenProvider` supports client secret only — no `private_key_jwt`, so no destination can
  obtain an Epic write token today. Reuse `SmartBackendServicesTokenProvider` + `BackendServicesJwtFactory`.
- Before the first write, check the token response `scope` (accept SMART v1, v2 and Epic API-name spellings) and
  report 400 `unauthorized_client` and 403 `insufficient_scope` as distinct configuration errors.

## 6. Phase 0b — sandbox test results (2026-09-30)

Run against `fhir.epic.com` with a dedicated Backend Systems app (SMART v2, JWK Set URL served by
`SourceJwksController`), using Epic's synthetic patients Desiree Powell (`eAB3mDIBBcyUKviyzrxsnAw3`, one
in-progress office visit) and Linda Ross (`eIXesllypH3M9tAA5WdJftQ3`, one finished inpatient stay). Stage A was
read-only; stage B made the writes below. Sandbox data is wiped every Sunday.

| # | Question | Result |
|---|---|---|
| 1 | Token request variants | Every variant returned 200 with the app's full registered set in v1 spelling; an unregistered type was silently dropped (section 4). |
| 2 | Default response; Location form | No `Prefer` → 201, empty body. Location relative (`Type/id`) on every create. No ETag. |
| 3 | Replays | Allergy, problem and note: a second record each time. Vital: 400 59189 "Reading already exists" (section 2). |
| 4 | AllergyIntolerance | RxNorm URL accepted; `code.text` replaced by the coding display; `category` and `recordedDate` not echoed. Not tested: `clinicalStatus` resolved, `verificationStatus` confirmed. |
| 5 | Condition | ICD-10-CM URL accepted with onset sent; `verificationStatus` confirmed → 422 59012. Not tested: onset omitted, resolved, OID forms. |
| 6 | Vitals | Both category URLs accepted; LOINC alone resolved the flowsheet row; `kg` accepted; time outside the encounter period accepted on an open visit. The finished-encounter test is **inconclusive**: it returned 59189 "Reading already exists" rather than an encounter error. |
| 7 | Clinical notes | `status` current + `docStatus` preliminary accepted; author defaults to the background user; a **finished** inpatient encounter was accepted. Not tested: wrong-patient encounter. |
| 8 | Patient.Create | 400 59108 `identifier (ssn)` — the sandbox requires an SSN, so no patient was created. New-vs-existing signalling and MRN assignment are untested. `$match` on the clone demographics returned 4101 before and after. |
| 9 | `$match` | Boolean flag only; 59013 is HTTP 400; full demographics needed for a certain match; Epic identifiers ignored (section 2). 59011 not seen. |
| 10 | Read-back of our own writes | 403, empty body — the read APIs for those types are not on the app, so holding-tank readability is untested. |

Still open, each needing another sandbox write or an app change:

- Patient.Create with a synthetic SSN: new-vs-existing signalling, the replay result and MRN assignment.
- A vital against Linda's finished stay at a time with no existing reading, to see the encounter-state error.
- Whether our own allergy and problem ids can be read before reconciliation — needs the AllergyIntolerance and
  Condition read APIs added to the app.

## 7. Phase 1 — what is built (dry run only)

In Phase 1 every run was a dry run: the writer shapes, validates and resolves each record, reads from the EHR
where resolution needs it, and reports what it would write. Phase 2 (section 8) replaced the hard-coded switch
with a per-type release; a dry run still sends nothing.

**How a record flows.** Source node → (optional Transformation node) → EHR Write-Back node
(`EhrWriteBackDestinationNode`, `dest-ehr-writeback` in the portal). Exactly one source node, no Mapping node, and
no De-identification node anywhere upstream; the graph validator refuses anything else. Records read from the
target EHR itself (same base URL) are reported as `already-in-ehr` and never sent back, because Epic would file
second copies; QA clone mode (Phase 3) is the only way to write an EHR's own data back into it.

| Step | Where |
|---|---|
| Resolve the target connection named by the node's `dest_sourceConnectionId`; refuse it unless it is enabled, its `Access` allows Write and its vendor is in `EhrWriteCapabilities` | `EhrWriteBackDestinationNodeExecutor` |
| Build the channel over the vendor connector; for Epic, send no `scope` (`FhirSourceConfiguration.OmitScopeParameter`) | `FhirClientEhrWriteChannel`, `SmartBackendServicesTokenProvider` |
| Skip unselected or unwritable types; shape each record to the API's accepted subset | `MappedEhrWriteBackDestinationWriter`, `Destinations/EhrWriteBack/Epic/*WriteProfile` |
| Resolve the patient: same EHR environment (ids kept) → ledger → identifier search (exactly one hit) → `$match` (certain only); resolve an encounter for notes (open or finished) and vitals (open only) | `EhrReferenceResolver` |
| Check the ledger, keyed on (EHR environment, resource type, source record) — not on connection or destination ids, which clones change | `EhrWriteLedgerEntry`, `IEhrWriteLedgerRepository` |
| Report counts and reason codes per resource type, and whether the granted scope covers what would be written | `EhrWriteReport` on the result; `ehrWrite` in node metadata and run history |

**Safety properties already in place for Phase 2.**
- A create is sent once. The connector's write loop retries only 429, and 503 with Retry-After. The HttpClient's
  resilience handler no longer retries unsafe methods on the vendor clients (`AddWriteSafeResilience`), so the
  host-wide Polly retry cannot resend a POST underneath it.
- A timeout, dropped connection, cancellation or 5xx on a create is `OutcomeUnknown` → ledger `Unknown`, never
  retried automatically. 59141 and 59189 (with expression `code/instant`) are recorded as already at target.
- Nothing PHI-bearing is logged or stored: outcome issues carry codes and element paths only (no diagnostics),
  record errors are `Type #n: reason-code`, identifier-search URLs are logged redacted.
- A connection set to Write only cannot be read as a workflow source (`FhirSourceConfiguration.AllowsRead`).
- A resend of a rejected row is claimed with optimistic concurrency (`AttemptCount`), so two runs cannot both
  send it. Rejections caused by configuration (no token, 401/403, 429, 503, Epic 59108) stay retryable.
- `Patient/$match` is read as no-match only when Epic says 4101 with no error; a single candidate counts only with
  score 1. An empty selection writes nothing, and a record without a source id is rejected.
- The configured-pipeline (route) plane cannot use this destination: it has no EHR channel, and the writer refuses.

**Configuration.** `SourceConnections.Access` (Read / Write / ReadWrite, default Read; Write only for a
write-capable vendor over Backend System). Destination settings: `dest_sourceConnectionId`, `dest_dryRun`,
`dest_createPatientIfMissing`, `dest_maxWritesPerRun` (default 500, max 10000, per write call),
`dest_noteDocStatus` (preliminary by default), and the node's `dest_resources`. Permission group `EhrWriteBack`
(`ehrwriteback.view/create/edit/delete/execute`). System setting `EhrWriteBack:CloneModeEnabled` (QA only,
seeded false; clone mode itself is Phase 3). API: `GET api/v1/ehr-write-capabilities?vendor=`. Migration
`AddEhrWriteBack` for both providers.

**Known limits, picked up in later phases.** Notes must already be plain text (HTML/RTF conversion is Phase 3);
vital units are passed through, not converted; a patient that would be created makes its other records skip as
`patient-not-yet-created` until the live create exists; `dest_maxWritesPerRun` caps one write call, not a whole
run. (The ledger review screen came in Phase 2.)

**Phase 1 acceptance run (2026-10-02, not completed).** The Epic → Epic workflow
("EHR Write-Back dry run (Epic to Epic sandbox)") and a Write-only target connection ("Epic Write-Back Sandbox",
client `de35f913…`, kid `fb-6f886de66`) were created in `FHIRBridge_v2`, and the workflow passed graph validation.
The run stopped at the source node: Epic answered `invalid_client` for the read app (`0184f963…`, kid
`fb-e181ab9db`) because its JWKS host (`segue.pegasusone.com:4003`) returned 502, and Epic itself then went into
downtime. To be re-run once both are back; with source and target on the same sandbox every record is expected to
report `already-in-ehr`.

## 8. Phase 2 — live writes for allergies, problems and notes

**Release, in two keys.** A record is sent only when all of these hold:

1. the destination is not a dry run (`dest_dryRun` is `false`; anything else is a dry run);
2. the code supports live writes for the type (`EhrWriteCapability.LiveWriteSupported`): Epic AllergyIntolerance
   (945), Condition problem-list item (949) and DocumentReference clinical note (1046). Vitals and patients stay
   dry-run-only until Phase 3;
3. an administrator released it in the system setting `EhrWriteBack:LiveWriteTypes`, a comma list of
   `Vendor:ResourceType` (e.g. `Epic:AllergyIntolerance,Epic:Condition,Epic:DocumentReference`), seeded empty.
   Entries for other vendors, unknown types and types the code does not support are ignored, so a bad value can
   only release less (`EhrWriteBackSettings.ReleasedResourceTypes`, read through `IEhrWriteReleasePolicy`).

Anything else is counted as `wouldWrite`; when the destination asked for live writes the reason
`live-write-not-released` says why it was not sent. The run reports `dryRun: false` only when at least one selected
type was live. `GET api/v1/ehr-write-capabilities` now returns `liveWriteSupported` and `liveReleased` per type, and
the destination form shows which selected types a live run would send.

**Review list.** A write whose outcome is unknown (timeout, reset, 5xx), that the EHR refused, or whose send never
finished (Pending for 15 minutes) is never retried on its own. Operations → EHR Write-Back Review
(`/operations/ehr-write-review`, permission `ehrwriteback.view`; resolving needs `ehrwriteback.edit`) lists them
with the target connection, HTTP status, vendor codes, attempt count and run id — no PHI. A reviewer checks the
chart and either:

- **It is in the EHR**: enters the EHR's id; the row becomes `Written` and guards against a second copy;
- **Not in the EHR**: releases it; the row becomes `Released` and the next run sends it once more.

`ReviewedBy` / `ReviewedOnUtc` record who did it. `State` is now a concurrency token alongside `AttemptCount`, so a
reviewer and a run touching the same row cannot both win. API: `GET api/v1/ehr-write-ledger/review`,
`POST api/v1/ehr-write-ledger/{id}/mark-written`, `POST api/v1/ehr-write-ledger/{id}/release`. Migration
`AddEhrWriteLedgerReview` for both providers.

**Sandbox verification (pending: Epic was down on 2026-10-02).** To be run by the user, in order, against
Desiree (`eAB3mDIBBcyUKviyzrxsnAw3`) with `EhrWriteBack:LiveWriteTypes = Epic:AllergyIntolerance`:

| # | Check | Expected |
|---|---|---|
| 1 | Phase 1 acceptance dry run (section 7) | every record `already-in-ehr`, scope `verified` |
| 2 | One allergy from a non-Epic source, live | 201, ledger `Written` with the Epic id |
| 3 | The same run again | no request; `already-written` |
| 4 | Release Condition, one problem live | 201 |
| 5 | Release DocumentReference, one note on the open visit live | 201, filed as preliminary |
| 6 | Force a timeout (very short client timeout) on one allergy | ledger `Unknown`, listed for review, not resent |
| 7 | Review: mark #6 written with the id seen in the chart | row `Written`, next run sends nothing |

## 9. Sources

- Sandbox CapabilityStatement: https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4/metadata
- Sandbox SMART configuration: https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4/.well-known/smart-configuration
- Interface catalog: https://open.epic.com/Interface/FHIR
- API specs (page / raw data): `https://fhir.epic.com/Specifications?api=<id>` / `https://fhir.epic.com/Specifications/Api?id=<id>`
  for ids 945, 949, 1046, 963, 930, 10423, 962, 974, 10050, 10051, 10303, 10023, 10090, 878
- OAuth 2.0 documentation: https://fhir.epic.com/Documentation?docId=oauth2
