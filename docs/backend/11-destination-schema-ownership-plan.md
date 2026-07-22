# Destination Architecture — Customer-Owned Schema: Removal & Validation Plan

> **Audience:** Claude Code (or any developer) told to "implement with this context file."
> **Created:** 2026-07-19, branch `feature/governancelogging`.
> **Context:** Segue is being productized as a standalone application deployed by customers through Azure Marketplace / AWS Marketplace / similar. Every customer owns and manages their own destination database (currently SQL Server; other relational engines expected later). A code-level architecture review found that the relational destination writers currently behave as if **Segue owns the destination schema** — auto-creating the customer's database, schema, and tables — which directly conflicts with the product's actual operating model. This document is the complete, self-contained plan to fix that: what's already been fixed, what still needs removing, and what validations (backend + UI) need to be added so Segue only ever writes into columns the customer explicitly mapped, never assumes or creates structure.

---

## Standing authorization — read before starting

When told to "implement with this context file," proceed through the items below in order **except** where a section is explicitly marked **⚠ Decision required before implementing** — those need a real go/no-go answer from a human first; don't assume a default and proceed silently.

Verification discipline: for anything touching real DB writes/reads (which is most of this plan), write a temporary `_Scratch*.cs` test under `tests/FHIRBridge.UnitTests/...`, run it against real infrastructure (SQL Server on `localhost,1433` per `docker-compose.yml`, or Postgres/MySQL if touching those writers), confirm it passes, then delete it. Confirm behavior against an actual database the test does **not** pre-provision via Segue itself — i.e. create the test table by hand (or via a raw `SqlCommand` in test setup, not via the writer under test) to genuinely simulate "customer already has a table," since that's the exact scenario this whole plan is about.

After finishing, update this document's "Current state" sections (or add a new numbered status doc, following the pattern of [08-governance-logging-status.md](08-governance-logging-status.md)) so the next person knows what's done.

---

## 0. Product assumptions this plan must satisfy

These are the non-negotiable constraints driving every item below:

1. Every customer owns and manages their own destination database.
2. Today's destination is SQL Server; future customers may use other relational engines (Postgres, MySQL already exist; more may be added later).
3. Segue has **no ownership or control** over the destination database schema.
4. Segue must **never assume** what tables, columns, constraints, indexes, triggers, or stored procedures exist in the customer's database.
5. Segue must **not create, modify, or delete** tables, columns, indexes, constraints, or any other database object in the customer's destination database.
6. Segue's only responsibility is to **write data into columns explicitly mapped** by the customer through the mapping configuration.

Corollary already agreed in this plan's discussion: all execution metadata, audit information, lineage, execution history, and operational tracking belongs entirely in **Segue's own database** — never in the customer's destination tables.

---

## 1. Current state — updated 2026-07-20

The four items below were already true when this document was verified fresh against the code on 2026-07-20 (a separate governance/observability logging commit on this branch had incidentally already made these changes as part of unrelated work). Section 5 step 1 (removing DB/schema/table auto-creation, section 3.A.1-2) has now also been completed and verified — see below.

### Already in place (confirmed 2026-07-20)

- **System/audit columns removed from destination tables.** `MappedSqlServerDestinationWriter` (SQL Server/Azure SQL) and `RelationalDestinationWriterBase` (Postgres/MySQL) no longer inject `FHIRBridgeRowId`, `PipelineRunId`, `ResourceType`, `SourceResourceId`, `WrittenOnUtc`, `LastUpdatedOnUtc` into `INSERT`/`MERGE`. A destination table now contains **only** the columns the mapping profile actually maps. Both `EnsureTableAsync` methods throw `InvalidOperationException` if the mapping profile has no mapped fields.
- **Upsert requires an explicit key column.** `ParseDestinationTarget`/`ParseTarget` throw `InvalidOperationException` at parse time if write mode is `Upsert` and no `?key=<ColumnName>` option is present — no implicit default to `SourceResourceId`. `TryGetKeyValue` in both writers does a plain lookup against the mapped `Values`.
- **Upsert's MERGE/DELETE no longer filters by `ResourceType`.** Match is purely on the configured key column.
- **The portal's destination-data preview no longer filters by `PipelineRunId`.** `IDestinationDataService.ReadSampleAsync`/`SqlDestinationDataService.ReadSampleAsync` return a plain top-N sample of the table; the `GET /workflows/{workflowId}/destination-data` endpoint no longer takes a `pipelineRunIds` parameter or depends on `IWorkflowRunStore`.

### Done today (2026-07-20) — section 5 step 1 (DDL/creation removal)

- **`SqlServerConnectionFactory.OpenConnectionAsync`** no longer opens a `master` connection or runs `CREATE DATABASE`; a missing database now surfaces as a normal SQL connection error.
- **`MappedSqlServerDestinationWriter.EnsureTableAsync`** no longer runs `CREATE SCHEMA`/`CREATE TABLE`. It now queries `OBJECT_ID` for the target table and throws `InvalidOperationException` ("...does not exist. Create it in your database before running this pipeline.") if missing, in addition to its existing zero-mapped-fields guard. The now-unused `GetSqlType` helper (only needed for generating `CREATE TABLE` column DDL) was removed.
- **`RelationalDestinationWriterBase.EnsureTableAsync`** (Postgres/MySQL) — same treatment. The `BuildCreateSchemaSql`/`BuildCreateTableSql` dialect hooks were replaced with a single `BuildTableExistsSql(schema, table)` hook; `MappedPostgreSqlDestinationWriter` queries `information_schema.tables` by schema+table name, `MappedMySqlDestinationWriter` queries it scoped to `DATABASE()` (MySQL has no schema layer distinct from the database). The now-dead `ExecuteAsync` helper (only used by the removed DDL calls) was removed.

**Verified:** full solution build clean; `FHIRBridge.UnitTests` (150 tests, one pre-existing unrelated failure in `DestinationExecutionHistoryGateTests` confirmed present before these changes too) and `FHIRBridge.ArchitectureTests` pass. A temporary `_ScratchDestinationSchemaOwnershipTests.cs` was run against the real `docker-compose` SQL Server (`localhost,1433`), PostgreSQL (`localhost:5433`), and MySQL (`localhost:3307`) containers — hand-creating each target table via raw ADO (not through the writer under test) to simulate "customer already has a table" — covering: missing-table throws without auto-creating anything, insert against a pre-existing table writes only mapped columns, and Upsert matches purely on the key column (a row is updated, not duplicated, even when `ResourceType` differs across calls). All 7 passed; the scratch file was deleted afterward per the verification discipline above.

### Done today (2026-07-21) — sections 2a, 2b, 3.E (sequencing steps 2, 3-partial, 8)

- **Architecture-test DDL guardrail (section 3.E).** Added `tests/FHIRBridge.ArchitectureTests/DestinationWriterNoDdlTests.cs`, following the existing `ApplicationTypeDispatchTests` pattern (locates `src/` via the `FHIRBridge.sln` anchor). It scans every `.cs` file under `src/FHIRBridge.Infrastructure/Destinations` and `src/BuildingBlocks/FHIRBridge.Integration/Sql` for `CREATE TABLE`/`CREATE DATABASE`/`CREATE SCHEMA`/`DROP TABLE`/`DROP DATABASE`/`DROP SCHEMA`/`ALTER TABLE`, failing on any unsuppressed match. The one known exception — CDC mode's still-open `EnsureCdcTableAsync` (section 3.A.3) — is annotated with a `// ddl-allowed: <reason>` comment a few lines above its `CREATE TABLE`, which the test explicitly looks for within a lookback window; any *other* DDL reintroduced anywhere else in those files fails the build unsuppressed. Verified passing (6/6 architecture tests, including this one).
- **Constraint/index metadata in schema introspection (section 2b).** `DestinationColumnSchemaDto` ([DestinationSchemaDto.cs](../../src/FHIRBridge.Application/DTOs/DestinationSchemaDto.cs)) gained `bool IsPrimaryKey = false` / `bool IsUnique = false`. `SqlDestinationSchemaService` ([SqlDestinationSchemaService.cs](../../src/FHIRBridge.Infrastructure/Destinations/SqlDestinationSchemaService.cs)) now runs a second query per dialect against `information_schema.table_constraints` + `key_column_usage` (SQL Server gets its own `INFORMATION_SCHEMA`-cased variant matching the existing column-query split), joined on schema+table+constraint name so MySQL's universal `PRIMARY` constraint name doesn't collide across tables. Results are merged into a `Dictionary<"schema.table.column", (IsPrimaryKey, IsUnique)>` (case-insensitive, OR-merged since a column can appear under more than one constraint) and consulted while building each `DestinationColumnSchemaDto`. Confirmed compiling and `FHIRBridge.UnitTests` unaffected (149/150, same pre-existing unrelated failure as before).
- **`IsUpsertKey` on `MappingField` (section 2a).** Added `bool IsUpsertKey = false` to the domain record ([MappingField.cs](../../src/FHIRBridge.Domain/ValueObjects/MappingField.cs)), the EF owned-type config ([MappingProfileConfiguration.cs](../../src/FHIRBridge.Infrastructure/Persistence/Configurations/MappingProfileConfiguration.cs), `IsRequired().HasDefaultValue(false)`), and `MappingFieldDto` + `ConfigurationMapper.ToDto/ToDomain` ([MappingFieldDto.cs](../../src/FHIRBridge.Application/DTOs/MappingFieldDto.cs), [ConfigurationMapper.cs](../../src/FHIRBridge.Application/Mappings/ConfigurationMapper.cs)) so it round-trips through the save/load API path today, ahead of the Angular control that will set it (section 4 item 5). Landed as a plain relational column on the existing `MappingFields` table — **not** inside the section 7 JSON blob, since §6.5 (JSON storage migration mechanism/data-migration decision) is still open and this field doesn't need to wait on it. Migration `20260720190010_AddMappingFieldIsUpsertKey` adds the single `bit NOT NULL DEFAULT 0` column (verified: `Up`/`Down` are a clean single-column add/drop, no other model drift).
  - Both `MappedSqlServerDestinationWriter.ParseDestinationTarget` and `RelationalDestinationWriterBase.ParseTarget` now resolve the Upsert key column via `mappingProfile.Fields.FirstOrDefault(f => f.IsUpsertKey && f.IsEnabled && <matches resource/destination-object scope>)` instead of solely a `?key=<ColumnName>` string option, per section 2a's "derived from this field, not a second independently-configured value" requirement.
  - **Compatibility decision made without a human sign-off, flagged here for review:** the `?key=` option is kept as a **fallback** (`ResolveUpsertKeyColumn(mappingProfile) ?? legacy ?key= option`), not removed outright. Reasoning: the Angular wizard (`workflow-build-assembler.service.ts`) still writes `;mode=upsert;key=<Column>` into `DestinationObject`, and no UI exists yet to set `IsUpsertKey` (that's section 4 item 5, sequenced *after* this step per section 5). Making the writers honor *only* `IsUpsertKey` today would have broken every already-configured Upsert destination at its next scheduled run, with no way for an admin to fix it until the wizard ships. The fallback should be deleted once section 4's UI lands and any live Upsert-mode mapping profiles have been re-saved with a field flagged `IsUpsertKey`.
  - Verified: solution compiles (`FHIRBridge.UnitTests`, `FHIRBridge.Runtime.UnitTests`, `FHIRBridge.ArchitectureTests` all pass at the same pass/fail counts as before this change — 149/150, 70/70, 6/6 respectively; `FHIRBridge.Api.IntegrationTests` could not be run in this session because a Visual Studio-hosted instance of `FHIRBridge.Api` held its build output locked — re-run that suite once the local API process is stopped).

### Still open (deliberately not touched in this pass)

- CDC mode's companion `{Table}_Cdc` table is still auto-created — ⚠ decision required (section 3.A.3).
- Postgres/MySQL Upsert is still implemented as DELETE-by-key + INSERT, not a genuine UPDATE/MERGE (section 3.A.4) — deferred to last per section 5. (SQL Server's writer already uses a real `MERGE`.) Note: `RelationalDestinationWriterBase`'s delete-by-key emulation stringifies the key value before binding it as a parameter, which fails against a non-text-typed key column on PostgreSQL (`operator does not exist: integer = text`) though MySQL tolerates it via implicit coercion — worth keeping in mind when this item is picked up.
- The legacy `?key=` fallback described above (remove once section 4 ships and existing Upsert profiles are migrated to `IsUpsertKey`).
- "Source EHR/System Type" virtual field (section 2c), save-time and run-time validation (section 3.C/D), the Angular wizard changes (section 4 — including the `IsUpsertKey` checkbox and the new IsPrimaryKey/IsUnique-aware NOT-NULL/type warnings now that the DTOs carry that data), mapping-as-JSON storage (section 7, blocked on §6.5), confidence auto-map (section 8), and parent/child FK destinations (section 9) are all still open, in the order given in section 5.

---

## 2. New domain concepts needed (blocking dependency for sections 3–5)

Three things don't exist in the codebase today and need to be added before the validations in section 3 can be built:

### 2a. `IsUpsertKey` on the mapping field
> **Decision locked (2026-07-20):** the property is named **`IsUpsertKey`** — a per-field `bool` on `MappingField` (resolves §6.2; formerly referred to as `IsKey` in earlier drafts).

`MappingField` ([MappingField.cs](../../src/FHIRBridge.Domain/ValueObjects/MappingField.cs), a 14-parameter record) has `IsRequired`/`IsEnabled` but no `IsUpsertKey`/`IsUnique`. Today "the key" only exists as a `?key=<ColumnName>` string embedded in `MappingProfile.DestinationObject`/`DestinationConfiguration.Target`, parsed at write time (see section 1). Add a structured `bool IsUpsertKey` to `MappingField` so:
- The mapping-profile save path can validate "at least one field is marked as the upsert key" without string-parsing a query option.
- The Angular wizard has a real field to bind a checkbox/radio to.
- The existing `?key=` write-time mechanism should be **derived from** this field (e.g. `ParseDestinationTarget`/`ParseTarget` stop reading a query-string option and instead the caller passes the key column name resolved from `mappingProfile.Fields.First(f => f.IsUpsertKey).TargetField`), not a second, independently-configured value that could drift out of sync with it.

### 2b. Constraint/index metadata in schema introspection
`DestinationColumnSchemaDto` ([DestinationSchemaDto.cs:37-42](../../src/FHIRBridge.Application/DTOs/DestinationSchemaDto.cs#L37)) currently carries `Name, DataType, MappingValueType, IsNullable, MaxLength` — no primary-key/unique-index/identity flag. `SqlDestinationSchemaService` ([SqlDestinationSchemaService.cs](../../src/FHIRBridge.Infrastructure/Destinations/SqlDestinationSchemaService.cs)) only queries `information_schema.columns` (`SqlServerColumnsSql` at lines 179-184; shared `InformationSchemaSql` for Postgres/MySQL at lines 22-27) — never `table_constraints`/`key_column_usage` or index catalogs. Add:
- `bool IsPrimaryKey` / `bool IsUnique` to `DestinationColumnSchemaDto`.
- For SQL Server: join against `sys.indexes`/`sys.index_columns` (or `INFORMATION_SCHEMA.TABLE_CONSTRAINTS` + `KEY_COLUMN_USAGE`, simpler and dialect-portable) alongside the existing column query.
- For Postgres/MySQL: join against `information_schema.table_constraints` + `information_schema.key_column_usage` filtered to `PRIMARY KEY`/`UNIQUE` constraint types.
- This is needed by: the Upsert-mode gate (section 3, item C.5) and the Upsert-strategy replacement (section 3, item A.4).

### 2c. "Source EHR/System Type" as a mappable virtual field
> **Decision locked (2026-07-20):** this field is **mandatory** — same treatment as `IsUpsertKey` (resolves §6.3: always required, not only-when-shared). It is presented **disabled/read-only on the source side** (it carries no JsonPath — it is a constant per profile) and the admin maps it onto a **destination** column. For now the written value is left **empty (placeholder)**; actual `SourceSystemType`/`Name` population is deferred to a later pass. Wire the field, its mandatory-mapping requirement, and its validation now — the value stays blank until the follow-up.

`MappedDestinationRecord` ([MappedDestinationRecord.cs](../../src/FHIRBridge.Application/DTOs/MappedDestinationRecord.cs)) carries `PipelineRunId, ResourceType, DestinationObject, SourceResourceId, Values, SourceJson` — nothing identifying which source connection/EHR vendor produced the record. Because a single `DestinationConfiguration` can already be shared across multiple `MappingProfile`s (each tied to exactly one `SourceConnectionId`), a destination table can legitimately receive rows from more than one EHR vendor (e.g. Epic + Healow + a flat-file feed all landing in the same warehouse table) with **no way to tell them apart** once written, since Segue no longer stamps any system column.

`SourceConnection.SourceSystemType` ([SourceConnection.cs:16,35](../../src/FHIRBridge.Domain/Entities/SourceConnection.cs#L16)) already holds exactly this vendor label (Epic, Healow, MEDITECH Greenfield, generic FHIR, HL7v2, flat file, DB, sample, webhook — the 9 source types from the root architecture doc). This value is **constant for every record a given `MappingProfile` produces** (one profile → one source connection), so it doesn't need JsonPath extraction like real FHIR fields — it's a lookup, not a transform.

Work needed:
- Expose "Source EHR/System Type" (and optionally the connection's `Name`, for customers with two connections of the same vendor type, e.g. two separate Epic instances for two hospitals) as a distinct, clearly-labeled **virtual field** in whatever list the mapping UI/backend uses to offer real FHIR JsonPath fields — not JsonPath-driven, just a constant resolved once per profile.
- Where `ConfiguredPipelineService` builds each `MappedDestinationRecord` (around `MapResourcesAsync`, [ConfiguredPipelineService.cs:493](../../src/FHIRBridge.Infrastructure/Pipeline/ConfiguredPipelineService.cs#L493)), reserve the mapped `TargetField` for this virtual field but write an **empty placeholder value for now** (do not populate). The wiring to resolve the owning `SourceConnection` (via `mappingProfile.SourceConnectionId`, already available in the loaded `ConfigurationSnapshot`) and supply its `SourceSystemType`/`Name` is a **deferred follow-up**.
- This is a "nothing gets written unless explicitly mapped" feature, same as every other field — Segue does not auto-stamp this into any column; the admin must map it to one, and that mapping is validated as required (section 3, item C.5-new).

---

## 3. Backend plan

### A. Remove the DDL/creation behavior

1. **`SqlServerConnectionFactory.OpenConnectionAsync`** ([SqlServerConnectionFactory.cs:11-46](../../src/BuildingBlocks/FHIRBridge.Integration/Sql/SqlServerConnectionFactory.cs#L11)) — remove the `master`-connection + `CREATE DATABASE` block entirely (currently: builds a `master` connection string, opens it, runs `IF DB_ID(@DatabaseName) IS NULL BEGIN EXEC(N'CREATE DATABASE ...') END`). After removal, connecting simply opens the target connection string as given; a missing database surfaces as a normal SQL connection error. This factory is shared by the writer **and** by `SqlDestinationSchemaService` (`SqlDestinationSchemaService.cs:165`), so this single change also makes the schema-preview/test-connection path provably read-only.
2. **`MappedSqlServerDestinationWriter.EnsureTableAsync`** (`CREATE SCHEMA IF NOT EXISTS` + `CREATE TABLE IF NOT EXISTS`, lines 91-134) and **`RelationalDestinationWriterBase.EnsureTableAsync`** (lines 87-105) — remove the creation SQL entirely. Replace with an existence check (query `information_schema`/`sys.objects` for the table) that throws a clear, specific exception ("Destination table 'dbo.Patients' does not exist. Create it in your database before running this pipeline.") if missing. If the run-time pre-flight validation (section 3.D) already ran moments earlier in the same execution, this can trust that result instead of querying twice — decide at implementation time whether `EnsureTableAsync` becomes a no-op when pre-flight already passed, or stays as a defensive re-check.
3. **CDC mode's `EnsureCdcTableAsync`/`InsertCdcRecordAsync`** (`MappedSqlServerDestinationWriter.cs:218-283`) — ⚠ **Decision required before implementing.** This feature is currently only possible by auto-creating `{Table}_Cdc`, which can't survive under this model. Two options, pick one before writing code:
   - (a) Retire CDC mode entirely until redesigned around a customer-provisioned history table, or
   - (b) Require the customer to pre-create `{Table}_Cdc` to a documented shape, and have Segue validate-not-create it (same treatment as the main table).
4. **Upsert's delete-by-key + insert emulation** (`RelationalDestinationWriterBase.WriteAsync:60-68`, `DeleteByKeyAsync:140-156`) — once section 2b's constraint metadata exists, replace with a genuine `UPDATE ... WHERE <key> = @key` (INSERT if zero rows matched) or a native `MERGE`/`ON CONFLICT` per dialect — **only** when introspection confirms the key column has a real unique constraint/index. If it doesn't, Upsert mode must be refused with a clear validation error (see section 3.C.5) rather than silently emulated via delete+insert (risky under this model: customer-side foreign keys, triggers, or identity columns Segue can't see could be broken by a physical delete). This is the **highest-risk item in this entire plan** — it changes write semantics for any destination already configured with Upsert mode. Plan a migration/rollout note (e.g. detect existing Upsert-mode mapping profiles at deploy time and flag any whose key column lacks a unique constraint, so the customer can add one before the behavior change takes effect) before shipping this change.

### B. Extend schema introspection
Covered in section 2b above — add `IsPrimaryKey`/`IsUnique` to `DestinationColumnSchemaDto`, sourced via `information_schema.table_constraints`/`key_column_usage` (or SQL Server catalog views) alongside the existing column query in `SqlDestinationSchemaService`.

### B2. Expose "Source EHR/System Type" as a mappable field
Covered in section 2c above.

### C. Save-time validation (config-time gate)

Add a validation step inside `ConfigurationService.AddMappingProfileAsync` ([ConfigurationService.cs:244-260](../../src/FHIRBridge.Application/Services/ConfigurationService.cs#L244)) and `UpdateMappingProfileAsync` (lines 262-286), alongside the existing `EnsureSourceSupportsResourceTypeAsync` call (line 248 / 267) — that's the natural slot since `DestinationId`, `DestinationObject`, and `Fields` are all already in scope there, before anything persists. Requires injecting `IDestinationSchemaService` into `ConfigurationService` (not currently wired there). For relational destination types only (`SqlServer, AzureSql, PostgreSql, MySql` — check `DestinationConfiguration.DestinationType`, mirroring `IsRelational` in `SqlDestinationDataService.cs`/`SqlDestinationSchemaService.cs`), the check must:

1. Confirm `DestinationObject` resolves to an existing table (via `IDestinationSchemaService.GetSchemaAsync`).
2. Confirm every mapped `Field.TargetField` matches a real column on that table.
3. Confirm every NOT-NULL, no-default column on that table has a corresponding mapped field.
4. Confirm at least one field has `IsUpsertKey = true` (section 2a).
5. **Confirm the "Source EHR/System Type" virtual field is mapped to a destination column** (section 2c) — **always required** (like `IsUpsertKey`), so any destination table stays traceable to the EHR/system each row came from. (The written value is an empty placeholder for now — the mapping itself is what is mandatory.)
6. If the configured write mode is Upsert, confirm the key column (`IsUpsertKey = true` field) carries a real unique constraint/index per the extended schema DTO (section 2b).
7. Throw a descriptive validation exception (same pattern as the existing `EnsureDestinationHasNoExecutionHistoryAsync` gate at `ConfigurationService.cs:232-242`) blocking the save on any failure, naming exactly which check failed.

### D. Run-time pre-flight validation (execution-time gate)

Add the **same validation** (ideally one shared method/service so save-time and run-time definitions can't drift apart) as a call in `ConfiguredPipelineService.ExecuteRouteAsync` ([ConfiguredPipelineService.cs:418-576](../../src/FHIRBridge.Infrastructure/Pipeline/ConfiguredPipelineService.cs#L418)), immediately before `destinationWriter.WriteAsync(...)` at lines 501-507. Both `destination` and `mappingProfile` are already resolved in-memory from the pre-loaded `ConfigurationSnapshot` at that point (no extra DB round trip needed to get them). A failure here already flows naturally into the existing per-route `try/catch` (lines 458-575), which marks that route `Failed` and records the error into the shared `errors` list — no new error-plumbing required.

Because this runs on every scheduled execution across potentially many tenants, **do not** re-run a live `INFORMATION_SCHEMA` query on every single route execution — cache the last-validated schema snapshot per destination (invalidated whenever the mapping profile is saved/updated, plus a periodic TTL to catch out-of-band drift the customer makes on their own side) rather than querying live on every run.

### E. Guardrail

Add a `FHIRBridge.ArchitectureTests` rule (the project already enforces layering + no-`switch` conventions — follow that existing pattern) asserting none of the destination-writer classes' SQL strings contain DDL keywords (`CREATE TABLE`, `CREATE DATABASE`, `CREATE SCHEMA`). Cheap, permanent regression protection against this exact class of bug reappearing.

---

## 4. UI plan (`portal/src/app/components/node-library/destination-wizard/`)

Reference files: `destination-wizard.component.ts` (820 lines), `destination-wizard.component.html`.

1. **Hard-require a successful, current probe before Save**, for every relational destination type (`SqlServer`, `AzureSql`, `PostgreSql`, `MySql`). Today `next()` (`destination-wizard.component.ts:358-361`) only triggers a probe attempt on the way out of step 1 — it doesn't hard-block Save if the admin changes connection details later without re-probing. Extend the gate through to the final Save action in step 4 (Review), not just step progression. `testConnection()` (lines 381-413) and `probeState()` already exist and do the actual probing (`schemaSvc.probe(...)` → `POST /api/v1/destinations/schema-preview`) — this is a gating change, not a new probing mechanism.
2. **Remove (or hard-disable) the free-text table-name fallback.** `hasSqlTables()` (lines 549-551) is `true` only when `isSql() && probeState() === 'ok' && sqlTables().length > 0`; when false, the template (`destination-wizard.component.html:339-357`) falls back to a free `<input>` for the table name with zero validation. Replace this fallback with a blocking message: "No tables found in this database. Create the destination table in your database first, then re-test the connection." Do not let an admin type an arbitrary table name for a relational destination.
3. **Remove the free-text column-name fallback the same way** (`destination-wizard.component.html:397-403`) — column targets must only ever come from `columnsForResourceTarget(r)` (lines 557-562), never typed.
4. **Surface type/length/nullability information already returned by the (soon-extended) schema DTOs** at mapping time: warn or block when a mapped field's value type doesn't match the destination column's `MappingValueType` bucket, and flag NOT-NULL destination columns with no mapped field, before the admin reaches the Review step.
5. **Add an "IsUpsertKey" designation control** per mapping row (checkbox/radio) binding to the new `MappingField.IsUpsertKey` (section 2a) — required to be set on at least one row before Save. This replaces the current invisible `?key=` string mechanism with something the admin actually sees and sets.
6. **Add "Source EHR/System Type" as a mappable field** in the mapping step (section 2c), alongside the real FHIR JsonPath fields. It is shown **disabled/read-only on the source side** (no JsonPath — a constant per profile) and the admin maps it onto a **destination** column, same interaction pattern as any other field. Its value is left **empty for now** (placeholder; the real vendor label is populated in a later pass). **Block Save until it is mapped**, same mandatory treatment as the key requirement (item 5). Optionally pre-suggest this mapping when the destination table has an obviously-named column (`SourceSystem`, `EhrType`, etc.) as a convenience — but the admin must still confirm it; never auto-write it without an explicit mapping.
7. **Gate Upsert mode**: if the admin selects Upsert as the write mode, block Save until a key field is designated **and** the (extended) schema probe confirms that column carries a real unique constraint — with a clear message telling them to add one on their own database if it's missing, rather than Segue silently emulating it.
8. **Update "Test Connection" copy/tooltip** to state it performs a read-only schema check only, now that section 3.A.1 makes this true end-to-end.

---

## 5. Suggested sequencing

1. Remove DB/schema/table creation from the connection factory and both relational writers (section 3.A.1-2). Contained, highest-value fix, unblocks everything else being "safe" to build on top of. **No dependency on anything else in this plan.**
2. Extend schema introspection with constraint/uniqueness metadata (section 2b / 3.B).
3. Add `IsUpsertKey` to `MappingField` **and** the "Source EHR/System Type" virtual field (sections 2a, 2c) — land together; both are "new mapping-time concept the UI needs a control for and the backend needs to validate."
4. Add save-time validation in `ConfigurationService` (section 3.C) — depends on steps 2 and 3.
5. Add run-time pre-flight validation with caching in `ConfiguredPipelineService` (section 3.D) — depends on step 4 existing as reusable logic.
6. Update the Angular wizard (section 4) — depends on steps 2 and 3 (needs the new fields/DTOs to bind to).
7. Decide and implement CDC mode's fate (section 3.A.3, ⚠ decision required) and replace delete+insert Upsert with real UPDATE/MERGE gated on confirmed constraints (section 3.A.4) — **highest risk item, do last**, after everything else is stable, with its own rollout plan for existing Upsert-configured destinations.
8. Add the architecture-test DDL guardrail (section 3.E) — independent, do anytime, ideally early so it protects the rest of the work as it lands.

Steps 1 and 8 are low-risk and independent — do these first regardless of how the rest is sequenced. Steps 2–6 are interdependent and should be treated as one connected body of work. Step 7 is deliberately last.

**Additional workstreams (sections 7 & 8, added 2026-07-20):** the JSON mapping storage (section 7) is independent and should ride the same migration as step 3 (when `IsUpsertKey`/Source-System are added to `MappingField`). The confidence auto-map (section 8) depends on steps 2b and 6 and slots in immediately **after** step 6 (the wizard changes).

---

## 6. Open decisions requiring a human answer before implementation

1. **CDC mode's fate** (section 3.A.3) — retire until redesigned, or require customer-provisioned companion table?
2. ~~**Naming/shape of the new `IsKey` property**~~ — **RESOLVED (2026-07-20):** named **`IsUpsertKey`**, a per-field `bool` on `MappingField`. See section 2a.
3. ~~**Whether "Source EHR/System Type" mapping is always required, or only when shared**~~ — **RESOLVED (2026-07-20):** **always required** (mandatory like `IsUpsertKey`, for every relational destination regardless of sharing). The written value is an empty placeholder for now; only the mapping is mandatory at this stage. See sections 2c / 3.C.5 / 4.6.
4. **Upsert migration behavior** (section 3.A.4) — for destinations already configured with Upsert mode today (delete+insert emulation), decide whether the switch to real UPDATE/MERGE is a silent behavior change, a feature-flagged rollout, or requires detecting and blocking/warning on any existing Upsert mapping profile whose key column lacks a unique constraint before the new code path goes live.
5. **Mapping-JSON storage migration** (section 7) — ⚠ decide whether existing `MappingFields` rows must be migrated into the new JSON column (data migration) or whether a reset is acceptable (dev/pre-prod only). Also confirm the mechanism: EF Core owned-JSON (`OwnsMany(...).ToJson()`) vs. a serialized `HasConversion<string>` column with a `ValueComparer`.
6. **Confidence auto-map location + threshold** (section 8) — ⚠ decide whether the scorer runs client-side in the wizard (both field lists are already loaded there) or behind a backend `POST /api/v1/mapping/auto-map` endpoint (more testable/reusable), and pick the match-confidence threshold plus the below-threshold behavior (leave target blank vs. best-guess).

---

## 7. Mapping stored as JSON (no `MappingFields` table)

> **Scope note:** this section covers destination-independence requirements **#4** ("mapping stored as JSON, no `MappingFields` table") and **#6** ("the same mapping JSON authored in the UI is used at workflow execution when writing to the destination"). It is **orthogonal to customer-owned schema** — it changes how Segue stores the mapping in **its own** database, not anything about the customer's destination. It is included here because the new `IsUpsertKey` (section 2a) and "Source EHR/System Type" (section 2c) fields ride inside this JSON, so landing them together avoids a second migration.

### 7.1 Current state
`MappingProfile.Fields` is persisted as a **separate relational table**: [MappingProfileConfiguration.cs:29-50](../../src/FHIRBridge.Infrastructure/Persistence/Configurations/MappingProfileConfiguration.cs#L29) declares `builder.OwnsMany(x => x.Fields, field => { field.ToTable("MappingFields"); ... })` — one row per mapped column, one physical column per `MappingField` property, a shadow `Guid Id` PK and a `MappingProfileId` FK (cascade). Confirmed present on `feature/governancelogging` (also visible in `FHIRBridgeDbContextModelSnapshot.cs` → `b1.ToTable("MappingFields")`). Nothing about the mapping is stored as JSON today.

### 7.2 Change
- **[MappingProfileConfiguration.cs:29-50](../../src/FHIRBridge.Infrastructure/Persistence/Configurations/MappingProfileConfiguration.cs#L29):** replace the `OwnsMany(...).ToTable("MappingFields")` block with EF Core owned-JSON — `builder.OwnsMany(x => x.Fields, b => b.ToJson());` — which serializes the whole `Fields` collection into a single `nvarchar(max)` JSON column on `MappingProfiles`. Drop the per-property `HasMaxLength`/`HasConversion` declarations (they no longer apply to a JSON document) and the `field.ToTable/WithOwner/HasKey/Property<Guid>("Id")` lines. **Keep** `builder.Navigation(x => x.Fields).UsePropertyAccessMode(PropertyAccessMode.Field)` — the aggregate's backing-field contract is unchanged. (Alternative if `ToJson()` proves awkward with the owned-collection value comparer: a single serialized-string column via `HasConversion<string>` + a `ValueComparer<List<MappingField>>`; `ToJson()` is preferred for lower boilerplate.)
- **Migration:** this repo uses **sequential** EF migrations (e.g. `20260711105222_InitialCreate` → … → `20260718181037_AddAlertEngine`), **not** the single-`InitialCreate` convention. Add a **new** migration (e.g. `StoreMappingFieldsAsJson`) that drops the `MappingFields` table and adds the JSON column to `MappingProfiles`, then regenerate `FHIRBridgeDbContextModelSnapshot.cs`. If §6.5 decides existing rows must be preserved, include a data-migration step (read `MappingFields` rows, serialize per profile into the new column) in the migration's `Up`.
- **`IsUpsertKey` (2a) + Source-System mapping (2c) live inside this JSON** — no additional columns needed; adding them to the `MappingField` record is sufficient once storage is JSON.

### 7.3 Requirement #6 ("same JSON used at execution") — already structurally satisfied, keep it that way
Execution reads the identical in-memory `MappingProfile.Fields` collection at run time (`ConfiguredPipelineService.MapResourcesAsync`, around [ConfiguredPipelineService.cs:493](../../src/FHIRBridge.Infrastructure/Pipeline/ConfiguredPipelineService.cs#L493)) that the authoring path persisted — there is one mapping engine and no duplicate authoring/runtime model, so switching the *storage* from a child table to a JSON column does not change what executes. Verification: after the switch, confirm a round-trip test — author a profile with array policies + `IsUpsertKey` + the Source-System mapping, reload, run a pipeline, and assert the deserialized JSON drives the write exactly as authored (watch that owned-JSON round-trips `ArrayPolicy`/`ArrayAncestors`/`Cardinality` faithfully).

### 7.4 Sequencing
Independent of the customer-schema work; can land alongside section 3 step 3 (when `IsUpsertKey`/Source-System are added to `MappingField`) so all three ride one migration. No dependency on sections 3.C/3.D/4.

---

## 8. Confidence-scored auto-mapping on "Add Fields" (per resource in the wizard)

> **Scope note:** destination-independence requirement **#7** — "a confidence score maps source and destination automatically as soon as **Add Fields** is clicked against each resource in the wizard." Net-new feature; nothing to remove. It must rank **only** against the live probed destination schema, tying it to sections 3.A/4 (no hardcoded destination structure).

### 8.1 Current state
In [destination-wizard.component.ts](../../portal/src/app/components/node-library/destination-wizard/destination-wizard.component.ts) the only add-field action is `addRow(resource)` ([line ~588](../../portal/src/app/components/node-library/destination-wizard/destination-wizard.component.ts#L588)): it appends **one** row, defaulting to the next not-yet-mapped catalog field and snapping `targetName` to that field's **built-in** `sqlColumn`/`csvColumn` — it does **not** match against the live destination columns and has **no score**. There is no bulk "Add Fields", no similarity/confidence logic anywhere in the portal.

The two inputs a matcher needs are already client-side:
- **Source fields:** `availableFields(r)` ([line ~622](../../portal/src/app/components/node-library/destination-wizard/destination-wizard.component.ts#L622)) → `catalogByResource()[r] ?? defFor(r).fields`, i.e. the array-aware FHIR catalog from `MappingCatalogService`.
- **Live destination columns:** `columnsForResourceTarget(r)` ([line ~558](../../portal/src/app/components/node-library/destination-wizard/destination-wizard.component.ts#L558)) → columns of the probed `sqlTables()` table chosen for the resource.

### 8.2 Change
- Add a per-resource/data-group **"Add Fields"** button (next to the existing add-row control) that calls a new `addFields(resource)`.
- `addFields(resource)` scores every `availableFields(resource)` entry against every `columnsForResourceTarget(resource)` column:
  - **Name similarity** — normalized token / edit-distance ratio between the field's suggested column name and `column.name` (case/underscore/space-insensitive).
  - **Type compatibility** — the field's `valueType` vs the column's data type (`MappingValueType` bucket from the extended schema DTO, section 2b).
  - Blend into a single **confidence %** (weights TBD; e.g. 0.7 name + 0.3 type).
- For each source field pick the best-scoring column **above the threshold** (§6.6), bulk-append an editable `MappingRow` with the confidence surfaced in the UI; **below threshold leave `targetName` blank** for manual selection. Every proposed row stays fully editable and removable (reuse `updateRow`/`changeBusinessField`/`removeRow`).
- **Rank only against the live schema** (`columnsForResourceTarget`) — never a hardcoded/built-in column list — consistent with sections 3.A/4.
- **Interplay with 2a/2c:** the auto-map may *propose* `IsUpsertKey` when the matched column is flagged primary-key/unique (section 2b), but the admin confirms it; it must **not** auto-satisfy the mandatory "Source EHR/System Type" mapping (section 2c) — that stays an explicit admin action.

### 8.3 Decision & sequencing
- Location/threshold are §6.6 (client-side TS vs backend `POST /api/v1/mapping/auto-map`). Recommended: client-side for v1 (both lists already loaded, no round trip); promote to a backend `IMappingSuggestionService` if the scorer is reused outside the wizard.
- Depends on section 2b (column type/constraint metadata) and section 4 (wizard already probes the live schema). Slots into section 5 **after step 6** (the wizard changes).

---

## 9. Parent/child relational destinations with foreign keys

> **Scope note (added 2026-07-20, in scope now):** customer destinations are frequently normalized — a parent table plus child tables linked by a foreign key, e.g. `dbo.Patient` ← `dbo.PatientName (PatientId → Id)`, `dbo.PatientTelecom (PatientId → Id)`. The wizard authors exactly this ("child of Patient via `PatientId` → `Id`"). Segue must write the parent row **and** its child rows with correct FK linkage, **purely from config**, assuming/creating nothing (sections 0 + 3.A still hold). The **§7 JSON is designed to carry this from the start.**

### 9.1 Current state (gap)
`MappingProfile` models a **single** `DestinationObject` (one table) — no structured parent/child/FK concept. The engine + `DefaultMappingMaterializer` *can* emit parent rows + child tables (via `ArrayPolicy.SeparateDestination`, keyed by an ordinal `RowIndex`), but the execution path flattens to `result.Values` and **discards** `Rows`/`ChildTables` (`ConfiguredPipelineService.MapResourcesAsync:1092`); `MappedDestinationRecord` is flat; the materializer is **never called**; the writers are single-table with no FK propagation. Note the engine's child rows are ordinal-indexed, **not** FK-linked to a parent business key — that linkage is the missing piece.

### 9.2 Config model (rides inside the §7 JSON)
The `MappingProfile` JSON gains a `destinationTables[]` block describing shape + relationships:
- `table` — qualified name (`dbo.Patient`, `dbo.PatientName`).
- `role` — `Parent` | `Child`.
- `arrayRoot` (Child only) — the FHIR array JsonPath that fans the child out (e.g. `$.name`, `$.telecom`); one child row per array element.
- `foreignKeyColumn` (Child only) — the child column that stores the parent key (`PatientId`).
- `referencesParentColumn` (Child only) — the parent column the FK points at; **must be the parent's `IsUpsertKey` column** (see §9.4).

Each `MappingField` keeps `DestinationObject` = the table it targets, so a field belongs to the parent or to a specific child. `IsUpsertKey` (§2a) marks the key on each table (the parent must have one; a child may have its own).

### 9.3 Write path
- Replace the flat `MappedDestinationRecord` payload with the `MaterializedDataset` shape (parent rows + child tables) that `DefaultMappingMaterializer` already produces, and **wire the materializer into `ConfiguredPipelineService`** instead of flattening to `Values`.
- Writer algorithm (generic, config-driven, per resource): resolve the parent key **value** from the parent's `IsUpsertKey` field → upsert/insert the parent row → for each `Child` table, for each `arrayRoot` element, set `foreignKeyColumn = parentKeyValue` and write. Every table/column name comes from config; nothing hardcoded.

### 9.4 Key constraint (ties to §2a)
Because Segue injects **no** identity/rowid column and doesn't own the schema, a child FK must reference a **mapped business key** the parent row actually carries — the parent's `IsUpsertKey` column (e.g. `Patient.Id` sourced from the FHIR resource id). This avoids any need to read back a DB-generated identity Segue can't see. Validation (§3.C) gains: for every `Child` table, confirm `referencesParentColumn` equals the parent's `IsUpsertKey` `TargetField`, and `foreignKeyColumn` exists on the child table per the live schema (§2b).

### 9.5 Sequencing
Design the §7 JSON to include `destinationTables[]` **from the start** (forward-compatible). The domain model + JSON land with §7 / section 5 step 3. The write-path wiring (materializer + FK resolution) is the largest piece — do it after single-table execution is green, alongside §3.D run-time validation.

### 9.6 ⚠ Open decision (added to §6)
7. **Child-row write semantics** — for a Child table, is each pipeline run a full replace-children-of-this-parent (delete child rows by FK = parentKey, then insert) or an upsert per child row on the child's own `IsUpsertKey`? Replace-by-FK is simpler and matches "re-materialize this resource"; per-child upsert needs each child to have its own stable key. Confirm before building §9.3.
