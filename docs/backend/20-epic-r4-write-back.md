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

**When a record goes live.** A record is sent only when all of these hold:

1. the destination is not a dry run (`dest_dryRun` is `false`; anything else is a dry run);
2. the destination selects the type in Data groups (`dest_resources`);
3. the code supports live writes for the type (`EhrWriteCapability.LiveWriteSupported`): Epic AllergyIntolerance
   (945), Condition problem-list item (949) and DocumentReference clinical note (1046); vitals and patients were
   added in Phase 3 (section 9). No eClinicalWorks or athenahealth type is live-capable yet.

There is no installation-wide release list. Until 2026-10-03 a third key, the system setting
`EhrWriteBack:LiveWriteTypes`, had to list each type before it could go live; it was removed at the product owner's
request (migration `RemoveEhrWriteBackLiveWriteTypes` deletes its row for both providers). Who may take a
destination off dry run is now decided by the EHR Write-Back permissions: `ehrwriteback.edit` to change the
destination (including clearing Dry run) and `ehrwriteback.execute` to run a workflow that writes to an EHR. Only
`EhrWriteBack:CloneModeEnabled` remains as a system setting.

Every route that stores, copies, arms or runs a write-back node checks these permissions (commit after b9d4bdd8):
POST `/workflows`, POST `/workflows/build` and PUT `/workflows/{id}` need `ehrwriteback.create` to add an EHR
Write-Back node and `ehrwriteback.edit` to change its write settings (`dest_dryRun`, `dest_resources`,
`dest_createPatientIfMissing`, `dest_cloneMode`, `dest_maxWritesPerRun`, `dest_noteDocStatus`,
`dest_sourceConnectionId`, the destination); copying a workflow that has one needs `ehrwriteback.create`; `/run`
checks `ehrwriteback.execute` on the node type; saving or activating a scheduled one (and changing its resource-type
criteria) needs `workflow.run` and `ehrwriteback.execute`. A workflow that writes into an EHR only ever runs for a
signed-in caller: it is never publicly launchable (saved as not public, enable-public-launch refuses it, the public
launch endpoints and anonymous `/run` refuse it), never run from an EHR launch callback, and never reachable through
a checkpoint URL. Still open: the EHR connection a write-back node targets is not itself permission-checked.

A selected type the code does not support live is counted as `wouldWrite`; when the destination asked for live
writes the reason `live-write-not-supported` says why it was not sent (runs recorded before the change may show
the retired `live-write-not-released`). The run reports `dryRun: false` only when at least one selected type was
live. `GET api/v1/ehr-write-capabilities` returns `liveWriteSupported` per type, and the destination form shows
which types a live run would send.

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
Desiree (`eAB3mDIBBcyUKviyzrxsnAw3`), selecting one type at a time in the destination's Data groups (written when this
section used the since-removed `EhrWriteBack:LiveWriteTypes` release; "Release X" below now means "select X"):

| # | Check | Expected |
|---|---|---|
| 1 | Phase 1 acceptance dry run (section 7) | every record `already-in-ehr`, scope `verified` |
| 2 | One allergy from a non-Epic source, live | 201, ledger `Written` with the Epic id |
| 3 | The same run again | no request; `already-written` |
| 4 | Release Condition, one problem live | 201 |
| 5 | Release DocumentReference, one note on the open visit live | 201, filed as preliminary |
| 6 | Force a timeout (very short client timeout) on one allergy | ledger `Unknown`, listed for review, not resent |
| 7 | Review: mark #6 written with the id seen in the chart | row `Written`, next run sends nothing |

## 9. Phase 3 — vitals, patients, clone mode and note conversion

**Vitals (963) and patients (930) are live-capable.** Both still need releasing (`Epic:Observation`,
`Epic:Patient`). Vitals are filed only on an open encounter of the matched patient (in-progress, then
arrived / triaged / on leave); a vital on a finished encounter was not sandbox-tested, so it stays skipped as
`no-eligible-encounter`. Units are passed through as sent.

**Patients created in the run.** A patient is created only when `$match` found no one, the destination opted
in (`dest_createPatientIfMissing`) and Patient is selected. Patients are processed first; the new
id is cached, so the patient's allergies, problems and notes are filed in the same run. A record whose patient is
not in the batch gets the patient fetched from the source and created on demand, through the same ledger-guarded
path. A patient create that is refused or of unknown outcome is tried once per run, and every record of that
patient is skipped as `patient-not-created` (or `patient-awaiting-review` when its ledger row is under review).
A dry run, or Patient not selected, leaves those records as `patient-not-yet-created`.

**QA clone mode** (`dest_cloneMode`, offered in the form only while `EhrWriteBack:CloneModeEnabled` is on; a run
asking for it while the setting is off fails). Every source patient is written as a synthetic clone
(`EhrClonePatient`): family name prefixed `Zztest`, birth date moved back 30–209 days, every identifier replaced
by one synthetic SSN in the 900–999 area (never issued by the SSA), phone and email dropped. The clone is
deterministic per source patient, so a re-run finds it through the ledger instead of creating another. Clone mode:

- skips the same-environment rule (it is the way to write an EHR's own records back into it);
- never searches or `$match`es, because the real patient is what must not be found;
- keys its ledger rows apart from real writes (`SourceKey(..., clone: true)`);
- refuses a clone whose create comes back with the original patient's id (Patient.Create is match-or-create):
  the row is recorded as rejected with `clone-matched-original` and nothing is filed against it.

A clone has no encounters, so its notes and vitals are skipped (`no-eligible-encounter`); Patient must be selected
for anything to be written.

**Notes in HTML or RTF** are converted to plain text (`EhrNoteText`): paragraphs, line breaks, list items and table
cells are kept; scripts, styles, RTF header tables, pictures and other destinations are dropped; RTF hex
(Windows-1252) and Unicode escapes are decoded. Other formats are rejected as `note-format-not-supported`, content
that is not base64 as `note-content-not-base64`, and a note with no text left as `note-empty-after-conversion`.

**Notes whose text is a link** (eClinicalWorks, and Epic itself, serve `attachment.url` → `Binary/{id}`) get their
text from the run's own source before shaping (`EhrNoteContent`), and the Binary's `data` and `contentType` replace
the link. The Binary comes first from the batch itself: when a bulk export feeds a workflow with an EHR write-back
node and DocumentReference was requested, `BulkExportPollService` keeps the export's Binary file (normally dropped as
unrequested), and the writer uses those Binaries as note text, never counting or reporting them as records. This is
the only route for eCW, whose "Backend - Bulk API" app tokens are refused on a plain `Binary/{id}` read (401 "No
valid token found", with or without `system/Group.read`). Failing that, the Binary is read through the same source
connection (`FetchMissingReferenceAsync("Binary", id)`, FHIR JSON, with a token that never carries the bulk-only
Group scope). Only `Binary/{id}`, relative or absolute under the source's base URL,
is followed; a link to another server, another resource type, or with a query string is rejected as
`note-content-url-not-on-source`. A read that fails is skipped as `note-content-fetch-failed` (tried again next
run); a Binary that is missing or empty is rejected as `note-content-not-found`. A CSV / SQL Table source has no
server, so a linked note from it stays `note-content-not-inline`. The source connection needs `Binary.read`
(already in the eCW scope list). The Binary is read on every run, dry runs included, because the content hash the
ledger compares is computed on the shaped note.

**Sandbox verification (pending: Epic down on 2026-10-02).**

| # | Check | Expected |
|---|---|---|
| 1 | Patient.Create with the clone SSN format (`9xx-xx-xxxx`) | 201, or 59108 if the build validates SSN ranges (then pick another synthetic scheme) |
| 2 | A vital on Linda's finished stay (`e2tX.zRRuP1elysuhOHkiqg3`) | tells whether finished encounters can be allowed for vitals |
| 3 | Clone mode, Desiree, Patient + AllergyIntolerance selected | clone created as `Zztest…`, the allergy filed on the clone, the real chart untouched |
| 4 | Re-run #3 | no requests: the clone and the allergy are found in the ledger |
| 5 | One HTML note and one RTF note on Desiree's open visit | filed as preliminary, readable text |

## 10. Phase 4 — non-FHIR sources (CSV / SQL Table)

A new Runtime source node, **CSV / SQL Table** (`TabularSourceNode`, portal tile `tabular`), reads rows and builds
FHIR resources from templates. Its output is an ordinary `ResourceBatch`, so the EHR write-back writer, FHIR
destinations and FHIR transforms take it unchanged.

**Where rows come from.**
- **CSV upload** (`POST api/v1/tabular-sources/files`, at most 10 MB and 50,000 rows). Parsed at upload
  (RFC 4180; comma, semicolon or tab detected from the header; quoted line breaks; BOM ignored), then stored
  only encrypted (`TabularSourceFiles.EncryptedContent`, AES-GCM through `IPhiFieldEncryptor`). It is deleted for
  real, not soft-deleted. The node keeps only the file id.
- **SQL query** on SQL Server, PostgreSQL or MySQL. The connection string is stored as a secret
  (`POST api/v1/tabular-sources/sql-connections`, vault `tabular-sources`); the node keeps only the reference.
  Only one `SELECT` / `WITH … SELECT` is accepted, with comments stripped, no `;` between statements and no
  data- or schema-changing keywords. It runs in a transaction that is always rolled back, declared `READ ONLY` on
  PostgreSQL and MySQL. A read-only login is still the recommended setup.

**Templates** (`tab_templates`, `TabularFhirTemplateEngine`): FHIR JSON with `{{column}}` placeholders and
formats `number`, `integer`, `boolean`, `date`, `datetime`, `base64` (note text), `lower`, `upper`. An element
whose column is empty is dropped, along with objects and arrays left empty, so one template serves rows that fill
different columns. Presets for Patient, AllergyIntolerance, Condition (problem-list item), Observation (vital sign)
and DocumentReference (clinical note) use conventional column names. A resource repeated on several rows with the
same id (a patient on each of their allergy rows) is kept once. `POST api/v1/tabular-sources/preview` renders the
first five rows and lists the columns the templates read that the table lacks.

**Identity for write-back.** A required **dataset key** (`tab_datasetKey`) becomes the source's stand-in base URL,
`urn:fhirbridge:tabular:<key>`, which the destination executor hands the writer for a Tabular upstream. The ledger
keys every record on it, so uploading a corrected copy of the file, or cloning the workflow, does not file rows
again. There is no source to fetch a missing patient from, so for write-back the rows must build the Patient too
(the Patient preset fills what `$match` needs: name, gender, birth date, phone, address).

**Errors and PHI.** Cell values never appear in a log or an error: a bad cell is reported as
`Row 7: column 'onset' is not a valid date for the AllergyIntolerance template.` The node reports `rowsRead`,
`rowsTruncated`, `rowErrorCount`, up to 20 `rowErrors` and `duplicatesDropped` in its metadata. A missing dataset
key, file, query or template fails the node instead of looking like an empty table.

**Permissions.** New group `tabularsources` (view, create, edit, delete, execute). Preview needs Edit, because it
decrypts data. A Tabular source has no source connection, so the run endpoint checks `tabularsources.execute` for
it in place of the per-vendor check; no license allow-list applies, since it names no EHR vendor.

**Limits.** A Tabular source feeds whole-resource FHIR destinations (FHIR servers and EHR write-back). It does not
feed a Mapping node into a SQL or file destination: mapping profiles are keyed on a source connection, which a
Tabular source does not have.

**Gate.** "A CSV of allergies lands in the Epic sandbox through the same writer, unchanged" is covered offline by
`TabularSourceTests.A_csv_of_allergies_reaches_the_ehr_write_back_writer_unchanged` (patient matched by
`$match`, allergy would be written). The sandbox run is pending Epic's return.

## 11. Phase 5a — eClinicalWorks (Healow), dry run only

**Discovery (2026-10-02, practice `JAFJCD`, `https://fhir4.healow.com/fhir/r4/JAFJCD`).** The certified FHIR server
(eCW FHIR Facade 1.6) declares one write in its CapabilityStatement: QuestionnaireResponse create. Its SMART
configuration nevertheless advertises `system/*.c` and `.u` for most clinical types — the same scope-versus-API gap
Epic showed in Phase 0. eCW's clinical writes are **contracted** APIs (interop@eclinicalworks.com), activated per
practice and dependent on the practice's build; their specifications are not public, and no eCW sandbox has been
shown to accept them. What is known (from a third-party integration guide, unverified):

| Resource | Operation | Build | Notes |
|---|---|---|---|
| AllergyIntolerance | create | 12.0.2+ | create only |
| Condition | create | 12.0.2+ | problems / diagnoses / history, filed on an open telephone encounter eCW manages |
| Observation (vitals) | create | 12.0.3.04009405+ | |
| Patient | create, update | 12.0.2+ | refused with 202 `INVALID_PATIENT_ALREADY_EXIST` when account number and birth date match |
| DocumentReference | create | 12.0.2+ | a transaction Bundle with an HL7 v2 MDM attachment, needs an encounter id — a different shape |

**What is built.** Vendor data and profiles only; the framework is unchanged:

- `EhrWriteCapabilities` lists Healow AllergyIntolerance, Condition (problem-list item), Observation (vital signs,
  open encounter) and Patient (opt-in), all create-only, Backend only, and **all `liveWriteSupported: false`**:
  every eCW run is a dry run until a practice sandbox passes the Phase 2 gate. Notes are left out.
- eCW has no `Patient/$match`, so patients resolve by identifier search only (`supportsPatientMatch: false`); a
  record whose patient no identifier finds is skipped as `patient-not-matched`, and patient creation cannot be
  reached. eCW honours requested scopes, so its write connection sends them (`requestsScopeOnTokenRequest`).
- `202` is recorded as already at target.
- Profiles (`Healow/HealowWriteProfiles.cs`) shape the same US Core subset the verified Epic profiles build, through
  `DelegatingEhrWriteProfile`; a profile is replaced on its own once eCW's contracted spec is in hand.
- The Healow connector already had the write path and the write-safe HTTP resilience from Phase 1; the source form
  offers Write access for an eCW Backend System connection from the capabilities API.

**To go live with eCW:** contract the write APIs, get a practice (or sandbox) activated, run the Phase 2 checks
there, then set the verified types `liveWriteSupported: true`; destinations that select them then write live.

## 12. Phase 5b — athenahealth: QuestionnaireResponse only, dry run

**Discovery (2026-10-02, preview `https://api.preview.platform.athenahealth.com/fhir/r4`).** The certified FHIR R4
API is read and search plus one create, **QuestionnaireResponse**. Two operations also write: FamilyMemberHistory
`$batch-write` and MeasureReport `$submit-care-gaps`; their definitions are not readable anonymously (403). The SMART
configuration lists no scopes. Allergies, problems, vitals, documents and patients are written through athenaOne's
proprietary REST API (`/v1/{practiceid}/…`), which is outside this work's FHIR-R4-only scope, so athena is not a
target for the five clinical types.

**What is built.**
- `EhrWriteCapabilities` lists athenahealth QuestionnaireResponse create (Backend only, dry-run-only, no
  `$match`, scopes requested). eClinicalWorks gets the same entry: QuestionnaireResponse create is also the one
  certified write in eCW's CapabilityStatement.
- `UsCore/UsCoreQuestionnaireResponseWriteProfile` (athena and eCW subclasses): sends a `completed` or `amended`
  response with its questionnaire canonical, patient, `authored` and answers (`linkId`, `text`, `value[x]`, nested
  items). Author, source, encounter, basedOn, partOf and `valueReference` answers point at the source system and
  are dropped. In-progress responses are skipped (`not-completed`); a missing questionnaire, patient or answer
  set is rejected.
- The questionnaire must be one the EHR defines; a response to a questionnaire known only to the source will be
  refused and recorded as rejected.
- The write-back form offers athena and eCW Backend connections as targets (only the types they accept).

**Not built, on purpose.** FamilyMemberHistory `$batch-write` and MeasureReport `$submit-care-gaps` are operations,
not creates: wiring them means a new channel call and a different idempotency story (a batch is one request for
many records), which is a framework change Phase 5 was not meant to make. They are the next candidates if athena
write-back is wanted, together with an athenaOne (non-FHIR) channel for the clinical types.

**To go live with athena:** verify QuestionnaireResponse create against the preview environment with a
questionnaire athena defines, then set `liveWriteSupported: true` for QuestionnaireResponse.

## 13. Sources

- Sandbox CapabilityStatement: https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4/metadata
- Sandbox SMART configuration: https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4/.well-known/smart-configuration
- Interface catalog: https://open.epic.com/Interface/FHIR
- API specs (page / raw data): `https://fhir.epic.com/Specifications?api=<id>` / `https://fhir.epic.com/Specifications/Api?id=<id>`
  for ids 945, 949, 1046, 963, 930, 10423, 962, 974, 10050, 10051, 10303, 10023, 10090, 878
- OAuth 2.0 documentation: https://fhir.epic.com/Documentation?docId=oauth2
