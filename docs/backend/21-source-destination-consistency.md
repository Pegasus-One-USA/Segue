# 21 — Source / destination consistency: sources read, destinations write

> Part of the [Backend Architecture Guide](README.md). Admin how-to: [EHR write-back user guide](../user-guide/ehr-write-back.md).
> **Status: built 2026-10-08 / 2026-10-09 on `feature/ehr-write-back-multi-vendor`, commits `a7a0b9f2` … `e349c08a`
> (everything after `4c956468`). Verified end to end on the local stack on 2026-10-09 (section 13).**
> Builds on [20 — EHR write-back](20-epic-r4-write-back.md); this file records what changed in how sources,
> destinations and their connections are set up, not the write-back rules themselves.

## 1. Purpose

Before this work a source and a destination were set up in different ways. A source form had an **Access** dropdown
(Read / Write / Read & Write), so an admin set up an EHR *write* login on a *source*. A destination chose its own
resource types and could ask the source to fetch types the source was never set up for. There was one "EHR
Write-Back" tile with a Dry run checkbox and a separate "Test as" dropdown. CSV / SQL sources took one wide query for
every type. The aim was that a source and a destination look and behave the same, and that each does one thing.

## 2. The user's model

1. **Add a source**: Epic, athenahealth, eClinicalWorks, a SQL database or CSV files, with a connection picked from
   **Source Connections** or created on the spot. The source says which resource types it reads.
2. **Click + to add a destination**: any destination, an EHR included, with a connection picked from **Destination
   Connections** or created on the spot. The destination writes a subset of the types its source reads.
3. **An EHR as a source is read; an EHR as a destination is written.** The same EHR can be both, through two
   connections (or one older Read & Write connection, which then appears on both pages).

## 3. Commit map

| Step | Commit | What |
|---|---|---|
| E | `a7a0b9f2` | CSV / SQL source: one query or file per resource type, Check, saved databases |
| C + A | `7424a2a7` | Sources only read; EHR write connections move to Destination Connections; `DepartmentId` on the connection |
| B | `0d50d5f2` | Resource types chosen on the source; destinations write a subset; save-time subset rule |
| B (fix) | `4a476817` | Patient kept as the search anchor; `POST /workflows` checks the subset rule too |
| E (fix) | `35445473` | CSV / SQL node reopens with its database, files and filter columns chosen |
| C (lists) | `9abd43cf` | One connection list per page; delete locking; Test only on database rows |
| D | `f4d72a2b` | One destination tile per EHR; Run mode: Live / Dry run / Test on FHIR server |
| D (fix) | `c14a5acd` | A test run's report says "Test run", not "Live run to Epic" |
| B (UX) | `8ff9be1d` | Destination Resource types start ticked with the source's types |
| — | `29c2e4a7` | FHIR repository destination skips a refused record instead of failing the run |
| — | `a06008d0` | 25 long-failing portal specs fixed (one real bug: System Settings tab for `ehrendpoints.view`) |
| D (UX) | `e349c08a` | Review and Destination Connections name the EHR ("EHR write-back — Epic") |
| F | — | End-to-end runs and these docs |

Steps were done in the order E → C + A → B → one list → D → F. A alone would have left nowhere to create a write
connection, so A and C shipped as one commit.

## 4. Step E — CSV / SQL source: one query or file per resource type

**Backend.** A CSV / SQL Table node now saves `tab_streams`: one entry per resource type with `resourceType`, its own
`template`, and either `query` (SQL) or `fileId` plus an optional `rowFilterColumn` / `rowFilterValue` (CSV, "only
rows where column = value", case and surrounding spaces ignored). Each query uses plain column names; the results
become one batch (the same resource from two entries is kept once). Parsing: `TabularStreams`
(`FHIRBridge.Application/Services/Tabular`); reading: `TabularRowReader`; running: `TabularSourceNodeExecutor`.

**Check** — `POST api/v1/tabular-sources/check` verifies every entry without reading a row:

- SQL Server: `sp_describe_first_result_set`; PostgreSQL / MySQL: the query inside `LIMIT 0`, under the existing
  read-only rules (`TabularSqlGuard`, rolled-back read-only transaction).
- A missing table, view or column, or a syntax error, comes back in the database's own words (only those error codes
  are passed on, never other server text). Template columns the query does not return are listed.
- A CSV entry is checked against the header recorded at upload.
- The portal saves the node only after a check of the current settings passed.

**Saved databases.** `TabularSqlConnection` entity (`FHIRBridge.Domain/Entities`), table `TabularSqlConnections`,
migration `AddTabularSqlConnections` (SQL Server, and PostgreSQL in `FHIRBridge.Infrastructure.Migrations.PostgreSql`).
Endpoints under `api/v1/tabular-sources/sql-connections`: `GET`, `POST`, `PUT {id}`, `DELETE {id}`, `POST {id}/test`.
The connection string stays a secret; replacing it rewrites the same secret, so every workflow using the database
follows. The node keeps `tab_sqlConnectionId` (plus the name, engine and secret reference for display and run).

**Portal.** `tabular-source-form` split into its own components: `tabular-database-picker` ("Read from database",
Test, New database), `tabular-database-editor`, `tabular-csv-files`, `tabular-stream-card` (one card per type), shared
`tabular-form-shared.scss`. Fix `35445473`: each `<option>` marks itself `[selected]`, because a `<select>`'s own
`[value]` was applied before its options (saved databases, files, columns) had loaded and the browser dropped it.

## 5. Steps C + A — sources only read; write connections under Destination Connections

**A — the Access dropdown is gone from every source form** (EHR vendor forms v1 and v2, Generic FHIR). A source save
sends no `Access`, `VendorWriteApisActivated` or `DepartmentId`; `/workflows/build` clears them for source nodes
(`spec.Source with { Access = null, … }`) and creates new source connections as Read. Re-saving an old source node
can therefore never downgrade a Write / Read & Write connection or clear its "Vendor write APIs activated".

**C — EHR write connections.** Still `SourceConnection` rows (`Access` = Write or ReadWrite), but created, listed and
edited from **Destination Connections**: Epic, eClinicalWorks and athenahealth through the existing vendor forms opened
in a *write purpose* (Backend System, Access Write, the "Vendor write APIs activated" switch, athena **Department ID**),
and a FHIR server through a new small form (`generic-fhir-write-connection-form`: name, FHIR base URL, no auth).

| Area | Change | Where |
|---|---|---|
| Listing | `GET api/v1/source-connections?access=read\|write` (also on `/paged`); a Read & Write row is in both; absent = all | `ConfigurationCatalogController`, `SourceConnectionAccessFilter` |
| Permissions | Write access on create needs `ehrwriteback.create`; turning write on later, or changing department / activation of a write connection, needs `ehrwriteback.edit`; ticking "Vendor write APIs activated" needs `ehrwriteback.edit` on create too (`AuthorizeVendorWriteActivationAsync`); deleting a connection that can write needs `ehrwriteback.delete` — each on top of the vendor's own permission | `ConfigurationsController.AuthorizeWriteAccessAsync`, `SourceConnectionsController` |
| Department | `SourceConnection.DepartmentId`, migration `AddSourceConnectionDepartmentId` (both providers). The node's `dest_targetDepartmentId` wins; the connection's is the fallback | `SourceConnection`, `EhrWriteBackDestinationNodeExecutor.ReadOptions` |
| Licence | Write-only connections do not count against the source-connection quota or allow-list; re-checked when one later gains read access | `LicenseUsageCountsProvider`, `LicenseEnforcementSaveChangesInterceptor` |
| Read paths | Scope sync, the Configured Pipeline and patient aggregation never read through a write-only connection; the pipeline reports a named error for such a route and runs the others | `EpicSourceConnectionScopeSyncService`, `ConfiguredPipelineService`, `PatientAggregationService` |
| Copy | Copying a workflow clones a Read & Write source connection as Read, never clones a write-only one; copied write-back nodes keep the original write connection | `WorkflowEndpoints` |
| Pickers | The Source Connections list (`ehr-read-connection-kind`) and the two mapping profile dialogs ask for `?access=read`. The builder's existing-source picker (`ehr-vendor-source-form`, v1 and v2) calls `getAll()` with no filter, because the name-collision check needs every connection, and drops `access === 'Write'` rows itself | portal services |

**One list per page (`9abd43cf`).** The two panels added at the bottom of the pages (database connections, EHR write
connections) were replaced, at the user's request, by one list per page:

- **Source Connections** = EHR / FHIR read connections + SQL databases. Columns Name, Type, Audience, Base URL, Client
  ID, Status, Action by, Action on. Filters: kind (All connections / EHR / Database), EHR, audience, status.
- **Destination Connections** = destinations + EHR write connections. Columns Name, Type, Address, Vendor write APIs,
  Status, Action by, Action on. Filter: type (All types, All destinations, All EHR write connections, or one type),
  status.
- One **New** button opens one picker (`connection-kind-picker`) with a heading per kind (Sources: EHR, Database;
  Destinations: Destinations, EHR). Search, sort and paging run across the merged rows (`connection-list-query.ts`,
  `connection-list-page.ts`).
- Each kind owns its loading, permissions, row actions and dialogs: `ehr-read-connection-kind`,
  `database-connection-kind`, `destination-configuration-kind`, `ehr-write-connection-kind`.
- **Delete / Edit locking.** New `GET api/v1/workflows/ehr-write-connection-usage` (UnifiedAdmin) returns the
  connection ids any workflow destination node or saved EHR write-back destination writes through; the existing
  read-usage endpoint (also UnifiedAdmin) gives the ids read through. The rules:
  - Source Connections (`ehr-read-connection-kind`): **Edit** is locked only while a workflow reads through the
    connection (a read-purpose save never touches the write side); **Delete** is locked while a workflow reads or
    writes through it.
  - Destination Connections (`ehr-write-connection-kind`): **Edit** is never locked (an in-use write connection must
    stay editable, e.g. to tick "Vendor write APIs activated"); **Delete** is locked while a workflow writes through
    it, or, on a Read & Write row, while a workflow reads through it.
  - Database rows are never locked. Destination rows keep their own, older rule (Delete off while the destination
    has execution history or is used in a workflow).
  - Both usage endpoints are UnifiedAdmin-only. For any other role the 401 / 403 is ignored and nothing is locked.
  - The DELETE itself does not re-check; this is a list-screen guard.
- **Test** is offered on database rows only: they are the only kind with a test endpoint today.

## 6. Step B — resource types are chosen on the source

**Source declares.** An EHR / Generic FHIR source node saves its list in `Resources` and the marker
`Resource types declared` = `"true"` (step "Resource types to read", `source-resource-type-picker`). A CSV / SQL node
needs no marker: `WorkflowNodeResourceTypes.ReadSourceDeclared` (`FHIRBridge.Application/Services/Workflows`) reads
its `tab_streams` entries, then the legacy `tab_templates`, then `Resources`. An older CSV / SQL node therefore counts
as declared and is subset-checked on save. `null` (an EHR / Generic FHIR node without the marker) means a legacy,
undeclared source.

**Destination chooses a subset.** `WorkflowResourceTypeSubsetRule.Check` runs on `POST /workflows/build`, `PUT` and
(from `4a476817`) plain `POST /workflows`. Every type in a destination's `dest_resources` or `dest_mappings` must be
read by the source node(s) that feed it through the canvas edges (their union). The refusal names the destination,
source(s) and types: *"Destination 'X' writes A, which its source 'Y' does not read. Add it to the source's resource
types or remove it from the destination."* The portal runs the same check first with the same message
(`upstream-source-v2.util.ts`, `findDestinationTypesOutsideSource`). Not checked by `WorkflowGraphValidator` (which also
runs at run time) or on copy, so a workflow saved before the rule still runs.

**EHR destination greys what it cannot take.** `ehr-write-type-grid` lists every type the source reads
(`ehr-write-type-grid.model.ts`, `classifyEhrWriteTypes`). In the wizard the greyed reasons are only:

- "Not accepted by {EHR}" ("Not accepted by this FHIR server" on the FHIR server tile);
- "Needs a CSV / SQL Table source" (every way the EHR files it needs the EHR's own ids);
- "Not read by this destination's source" (only for an already-selected type the source no longer reads).

A type that only needs an option is **not** greyed: the wizard passes `optInsLater: true`, so it stays tickable with
the note `Needs "X" turned on under Options (next step)`, and the Options step then holds Next until that option is
on (`missingOptIns`). The "Enable … under Options" / `Turn on "File medical and surgical history on a new telephone
encounter" under Options` reasons exist in the model but are not shown in the wizard. Other notes on tickable types:
"Sent as a dry run until Vendor write APIs activated is ticked on the connection", "Sent as a dry run only for now"
(regardless of the connection's activation), "Created only when "Create the patient when the EHR has no match" is on
under Options".

**Pre-tick (`8ff9be1d`).** A *new* destination whose source declares its types starts Step 2 with every source type
ticked ("These are the N resource types your source reads. Untick any this destination doesn't need."). An EHR ticks
only types it accepts without an extra option. A reopened destination keeps its saved selection; a legacy source
starts unticked; the pre-tick happens once. The cards no longer show permission codes.

**Runtime.** The source still fetches only what its destinations chose (narrowing, never widening). Fix `4a476817`:
when a destination writes, say, AllergyIntolerance + Condition from a source that reads Patient too, Patient is still
searched as the anchor for the patient-compartment searches (otherwise they ran unscoped and Epic answered 403), but
its records are not handed on and its sync cursor is not advanced (`SourceNodeExecutors`).

**Scopes.** `EpicSourceConnectionScopeSyncService`: a declared source is scoped to its own list (plus auto-fetch
reference targets, the one documented exception); a legacy source to what its reachable destinations write, mapping
rows included.

## 7. Step D — one tile per EHR, Run mode

**Tiles.** The Node Library shows an **EHR** heading with Epic, eClinicalWorks, athenahealth and FHIR server tiles
(`transforms-v2.data.ts`: `dest-ehr-group`, `dest-ehr-epic` / `-ecw` / `-athena` / `-fhir`, `ehrTileFor`) in place
of the single EHR Write-Back tile. Every destination follows **Connection → Resource types → (Map fields | Options) →
Review**, and Step 1 reads "Write to …". Saved nodes keep `transformId` `dest-ehr-writeback`; the tile is recovered from
`dest_testAsVendor` / `dest_ehrVendor` (`savedWriteVendorOf`, `ehr-write-back.model.ts`).

**Run mode** (`ehr-run-mode-picker`) replaces the Dry run checkbox and the Test as dropdown. No new key is stored:

| Run mode | `dest_dryRun` | `dest_testAsVendor` | Connection offered (`connectionsFor`) |
|---|---|---|---|
| Live: write into {EHR} | `false` | empty | the EHR's own write connections |
| Dry run: check every record, send nothing | `true` | empty | the EHR's own write connections |
| Test on a FHIR server (Epic, eCW, athena only) | `false` | the EHR | FHIR server write connections that can stand in for it |

The radios are listed Live, Dry run, Test (`runModesFor`); a new destination has no saved `dest_dryRun`, so Dry run
is preselected. A FHIR server tile offers Live and Dry run only. Reading back (`runModeOf`): anything but an explicit
`dest_dryRun` = `false` is a dry run, as in the executor. A legacy node with **both** a dry run and a test vendor
reopens as a plain **Dry run** without losing its connection (`droppedTestServer` tells the form the saved test server
no longer fits). Test always sends to a FHIR server write connection; the backend still refuses a test run on any
other connection type (doc 20, section 14.2).

**Connection.** `ehr-write-connection-picker` ("Write to", or "Test server" in a test run) lists the write
connections fitting the EHR and run mode; **New connection** creates one in place (`ehr-write-connection-create`,
shared with Destination Connections), offered only to a role with `sourceconnections.view` plus `ehrwriteback.create`
and the vendor's own `.create`. The optional "Copy settings from a saved {EHR} destination (optional)" box copies a
saved destination's settings; it shows only when the destination is created from the canvas
(`showConnectionModeToggle`), not when an existing one is reopened.

**Options** (Step 3): `ehr-write-general-options` (Max writes per run, Clinical notes are filed as "Preliminary (a
clinician reviews and signs)" or "Final (signed under the integration user)", Create the patient when the EHR has no
match), `ehr-ecw-write-options` (the history-on-a-telephone-encounter checkbox only when the connection offers holder
encounters, `offersHolderEncounter`), `ehr-athena-write-options` (Department id placeholder shows the connection
default; with no department on the node or the connection, new patients are rejected), `ehr-opt-in-apis` ("Also write through these … APIs"), `ehr-clone-mode-option`. A new write-back
is saved once, on leaving Options.

**Review.** An EHR write-back's Review step shows the cards Destination type, De-identification, Name, Writes to,
Mode, Patients and Resource types.

- `e349c08a` changed the **Destination type** card: `reviewDestLabel()` returns
  `ehrWriteBackLabel(ehrVendor() ?? savedWriteVendorOf(activeFormConfig()))`, e.g. "EHR write-back — Epic" (a test
  run is named by the EHR it stands in for). It used to read "EHR Write-Back".
- The same commit names the EHR in the Destination Connections Type column of a saved write-back destination and in
  the "Copy settings from" list, both through `savedWriteVendorOfMetadata`. Write connection rows read "EHR write
  connection — Epic".
- **Writes to** and **Mode** (`ehrReviewLines`, from `f4d72a2b`) read e.g. "{connection} (Epic)" and "Dry run: checks
  every record, sends nothing, up to 500 records per run"; a test run reads "{connection} (FHIR test server), shaped
  as Epic" and "Test on FHIR server: nothing reaches Epic, …".
- The wizard binds only the inputs a Step 1 form declares (no NG0303).

**Report (`c14a5acd`).** The runtime `EhrWriteSummary` (`DestinationWriteResult.cs`) lacked `TestRun`, so a test run's
stored report read "Live run to Epic". It is now carried through (`DestinationNodeExecutors`). The report dialog title
is "Dry-run report" / "Test-run report" / "Write-back report" (`ehr-write-report-dialog.component.ts`) and its badge
"Dry run" / "Test run" / "Live"; the Copy report text says "Dry run" / "Test run" / "Live run" (`ehrWriteRunMode`). The
Execution History button reads "View dry-run report" only for a dry run; a test run's reads "View write-back report"
(`execution-history-detail.component.ts`).

## 8. FHIR repository destination: per-record refusals (`29c2e4a7`)

Applies to the **Aidbox** tile (`dest-fhir`, `FhirRepository`) and the **Azure FHIR Service** tile
(`AzureFhirService`); both use `MappedFhirRepositoryDestinationWriter` (`ConfiguredDestinationWriterFactory`). It does
**not** apply to the EHR group's "FHIR server" tile, which uses the EHR write-back writer.

`MappedFhirRepositoryDestinationWriter`, one-PUT-per-record mode only (write mode "Upsert by resource id (PUT)", i.e.
`dest_fhirWriteMode` absent or `individual`): a 400 / 409 / 412 / 422 now skips that record,
reported with the server's reason; a later record that references a refused or skipped one is never sent (no broken
reference is written) and is reported too; the run ends **PartialSuccess**. 401 / 403 / 404 / 405 / 429 / 5xx and
network errors still fail the run. Seen with HAPI-1094 on an Epic Observation whose `focus` points at another
Observation of the same batch. Batch-bundle mode already reported per record; `dest_fhirWriteMode` = `transaction`
keeps mutual references.

## 9. Saved node fields (reference)

| Node | Field | Meaning |
|---|---|---|
| EHR / Generic FHIR source | `Resources` | Types read |
| | `Resource types declared` | `"true"` = the list is declared (subset rule and scopes apply); absent = legacy |
| CSV / SQL source | `tab_kind` | `sql` or `csv` |
| | `tab_streams` | `[{ resourceType, template, query \| fileId, rowFilterColumn?, rowFilterValue? }]` |
| | `tab_sqlConnectionId` | Saved database (`TabularSqlConnections`) |
| | `tab_sqlConnectionName`, `tab_sqlEngine`, `tab_secretKeyVaultName`, `tab_secretName` | The saved database's name, engine and secret reference, for display and run (SQL only) |
| | `Resources` | The ticked types, comma-separated; read by the node library and, after `tab_streams` / `tab_templates`, by `ReadSourceDeclared` |
| | `tab_datasetKey`, `tab_maxRows` | Unchanged (doc 20, section 10) |
| | `tab_query`, `tab_fileId`, `tab_templates` | Legacy single-query form; still runs |
| Any destination | `dest_resources` | Types written (must be a subset of the source's declared types) |
| EHR write-back | `dest_sourceConnectionId` | The write connection (name kept for compatibility) |
| | `dest_dryRun`, `dest_testAsVendor` | Run mode (table in section 7) |
| | `dest_ehrVendor` | The EHR written to; with `dest_testAsVendor`, picks the tile |
| | `dest_targetDepartmentId` | athena; overrides the connection's `DepartmentId` |
| | others | `dest_maxWritesPerRun`, `dest_noteDocStatus`, `dest_createPatientIfMissing`, `dest_targetProviderId`, `dest_createHolderEncounter`, `dest_enabledVariants`, `dest_cloneMode` — unchanged |

## 10. Decisions and why

- **Sources only read; destinations write.** A "common user" found an Access dropdown on a source confusing. Listing is
  by capability: Read / Read & Write on Source Connections, Write / Read & Write on Destination Connections.
- **No data migration.** Existing Write / Read & Write connections keep working as they are.
- **Write connections stay `SourceConnection` rows.** Auth, token caching, scope reporting and the ledger already key on
  them; a new entity would have duplicated all of it.
- **Write-only connections are outside the source licence.** They read nothing.
- **Keep the Resource types step on SQL / NoSQL destinations.** One source (say seven types) can feed several
  destinations that each take a subset (SQL three, MongoDB two). The step is pre-ticked, not skipped.
- **Keep Dry run.** A dry run checks every record against the *real* EHR — patient `$match`, encounter lookup, the
  ledger, the granted scope — and sends nothing. Test on FHIR server writes to a different server, so it cannot prove
  any of that (doc 20, section 14.2, "What a test run does not prove"). Epic writes cannot be undone (no delete, no
  update for allergies and problems), so a check against the real target before the first Live run stays the
  recommended step, and Dry run stays the default mode.
- **Patients are matched, never guessed.** No change; it is why a Live write to a FHIR server needs findable patients
  (section 12).
- **Test only on database rows** until EHR read / write and destination test endpoints exist; a Test button that did
  nothing real would mislead.
- **Saved write-back nodes keep `dest-ehr-writeback`.** The tile is derived, so no saved workflow had to change.

## 11. Compatibility of saved and legacy workflows

| Saved state | Behaviour now |
|---|---|
| EHR / Generic FHIR source node without `Resource types declared` | Legacy: fetch narrowed to the destinations' types as before; no subset check; reopening shows a note, and changing the list declares it |
| Source node still carrying `Access` | Ignored on save; the connection's access is never changed from a source |
| Source node bound to a write-only connection | On build or copy the binding is kept untouched (the connection is not rewritten), so the rest of the workflow still saves; the run refuses to read through it until the node is pointed at a readable connection |
| CSV / SQL node with `tab_query` / `tab_fileId` + `tab_templates` | Runs unchanged; opens as one entry per template reading the same query or file; saving rewrites it as `tab_streams`. Counts as declared (its `tab_templates` types, or `Resources`), so the subset rule applies on its next save |
| Write-back node with `dest_dryRun` = true and a `dest_testAsVendor` | Reopens as Dry run; the test server is dropped from the form; runs as before until re-saved |
| Write-back node with only `transformId` `dest-ehr-writeback` | Tile from `dest_testAsVendor` / `dest_ehrVendor`; "EHR write-back" when neither names a vendor |
| Read & Write connection | On both pages; delete locked while a workflow reads or writes through it |
| Workflow saved before the subset rule | Still runs; the rule applies on its next save |

All 14 workflows that existed before the change were re-saved through the API and re-run on 2026-10-08 with their
original patient selection; none changed its connection and all ran. (Recorded in the step F working notes, not in
git history.)

## 12. Limitations

- **Live write to the FHIR server tile needs patients findable by identifier.** Patients are resolved by identifier,
  then `Patient/$match` (Generic FHIR declares `supportsPatientMatch: true`, `EhrWriteCapabilities`). A patient is
  created only when `$match` answers an explicit "no one" (`EhrPatientMatchKind.None`: Epic's issue code 4101 with no
  error alongside it, `FhirSourceConnectorBase.Write.cs`). A server without `$match` (a local HAPI without MDM)
  answers with an error instead, so the record is reported "Patient match failed", or "Uncertain patient match, left
  for review" when a 400 carries only processing issues (`EhrReferenceResolver`). Neither creates the patient, so
  "Create the patient when the EHR has no match" does not help there. In a **Test** run creation does work: "not found" is
  treated as None (`EhrReferenceResolver`, `_testRun`), so the patient is created when that option is on.
- **Test is on database rows only.** No test endpoint for EHR read / write connections or destinations yet.
- **Delete locking is a list-screen guard.** The DELETE endpoints do not consult `ehr-write-connection-usage`.
- **The subset rule is a save-time rule.** A workflow edited outside the API save endpoints is caught only at its next
  save; the runtime never widens the fetch either way.
- **CSV Check** compares columns with the header recorded at upload; it does not read data rows (cell format errors
  appear at run time, as `Row n: …` without values).
- **A test run does not prove the vendor's own validation** (unchanged, doc 20, section 14.2).

## 13. Verification (step F, 2026-10-09)

Eleven fresh workflows, "E2E-F f01" to "E2E-F f11", created through the API on the local stack and run; all passed:

| Case | Source → destination | Run mode |
|---|---|---|
| SQL → Epic | CSV / SQL (SQL, several types) → Epic | Test on FHIR server |
| SQL → eClinicalWorks | SQL → eCW | Test on FHIR server |
| SQL → athenahealth | SQL → athena | Test on FHIR server |
| SQL → FHIR server | SQL → FHIR server tile | Dry run, and Live |
| CSV row filter → eCW | One CSV file, rows split by a type column → eCW | Test on FHIR server |
| Epic subset → Epic | Epic source, destination writes a subset | Test on FHIR server |
| Epic → athenahealth | Epic → athena | Test on FHIR server |
| Epic → SQL Server | Epic → SQL Server (mapping) | — |
| Epic → MongoDB | Epic → MongoDB | — |
| athena → SQL Server | athenahealth → SQL Server | — |

API checks: Check errors for a missing table / column; subset refusal on `build`, plain create and `PUT`; connection
lists with `?access=read|write`; the athena Department ID fallback (node value wins, connection value used when the
node has none). The FHIR repository change was confirmed on a run with the Epic sandbox test patient: PartialSuccess
with the refused records reported instead of a failed run (recorded in the step F working notes, not in git history).

Test suites after the last commit, as reported at the end of step F (not re-run while writing this doc): unit 2489,
runtime 478, architecture 8, API integration 190, portal 1235 / 1235.
