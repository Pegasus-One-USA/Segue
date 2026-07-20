# Destination Architecture — Customer-Owned Schema: Removal & Validation Plan

> **Audience:** Claude Code (or any developer) told to "implement with this context file."
> **Created:** 2026-07-19, branch `feature/governancelogging`.
> **Context:** FHIRBridge is being productized as a standalone application deployed by customers through Azure Marketplace / AWS Marketplace / similar. Every customer owns and manages their own destination database (currently SQL Server; other relational engines expected later). A code-level architecture review found that the relational destination writers currently behave as if **FHIRBridge owns the destination schema** — auto-creating the customer's database, schema, and tables — which directly conflicts with the product's actual operating model. This document is the complete, self-contained plan to fix that: what's already been fixed, what still needs removing, and what validations (backend + UI) need to be added so FHIRBridge only ever writes into columns the customer explicitly mapped, never assumes or creates structure.

---

## Standing authorization — read before starting

When told to "implement with this context file," proceed through the items below in order **except** where a section is explicitly marked **⚠ Decision required before implementing** — those need a real go/no-go answer from a human first; don't assume a default and proceed silently.

Verification discipline: for anything touching real DB writes/reads (which is most of this plan), write a temporary `_Scratch*.cs` test under `tests/FHIRBridge.UnitTests/...`, run it against real infrastructure (SQL Server on `localhost,1433` per `docker-compose.yml`, or Postgres/MySQL if touching those writers), confirm it passes, then delete it. Confirm behavior against an actual database the test does **not** pre-provision via FHIRBridge itself — i.e. create the test table by hand (or via a raw `SqlCommand` in test setup, not via the writer under test) to genuinely simulate "customer already has a table," since that's the exact scenario this whole plan is about.

After finishing, update this document's "Current state" sections (or add a new numbered status doc, following the pattern of [08-governance-logging-status.md](08-governance-logging-status.md)) so the next person knows what's done.

---

## 0. Product assumptions this plan must satisfy

These are the non-negotiable constraints driving every item below:

1. Every customer owns and manages their own destination database.
2. Today's destination is SQL Server; future customers may use other relational engines (Postgres, MySQL already exist; more may be added later).
3. FHIRBridge has **no ownership or control** over the destination database schema.
4. FHIRBridge must **never assume** what tables, columns, constraints, indexes, triggers, or stored procedures exist in the customer's database.
5. FHIRBridge must **not create, modify, or delete** tables, columns, indexes, constraints, or any other database object in the customer's destination database.
6. FHIRBridge's only responsibility is to **write data into columns explicitly mapped** by the customer through the mapping configuration.

Corollary already agreed in this plan's discussion: all execution metadata, audit information, lineage, execution history, and operational tracking belongs entirely in **FHIRBridge's own database** — never in the customer's destination tables.

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

### Still open (deliberately not touched in this pass)

- CDC mode's companion `{Table}_Cdc` table is still auto-created — ⚠ decision required (section 3.A.3).
- Postgres/MySQL Upsert is still implemented as DELETE-by-key + INSERT, not a genuine UPDATE/MERGE (section 3.A.4) — deferred to last per section 5. (SQL Server's writer already uses a real `MERGE`.) Note: `RelationalDestinationWriterBase`'s delete-by-key emulation stringifies the key value before binding it as a parameter, which fails against a non-text-typed key column on PostgreSQL (`operator does not exist: integer = text`) though MySQL tolerates it via implicit coercion — worth keeping in mind when this item is picked up.
- Constraint/index metadata in schema introspection (section 2b), `IsKey` on `MappingField` (section 2a), "Source EHR/System Type" virtual field (section 2c), save-time and run-time validation (section 3.C/D), the Angular wizard changes (section 4), and the architecture-test DDL guardrail (section 3.E) are all still open, in the order given in section 5.

---

## 2. New domain concepts needed (blocking dependency for sections 3–5)

Three things don't exist in the codebase today and need to be added before the validations in section 3 can be built:

### 2a. `IsKey` on the mapping field
`MappingField` ([MappingField.cs](../../src/FHIRBridge.Domain/ValueObjects/MappingField.cs), a 14-parameter record) has `IsRequired`/`IsEnabled` but no `IsKey`/`IsUnique`. Today "the key" only exists as a `?key=<ColumnName>` string embedded in `MappingProfile.DestinationObject`/`DestinationConfiguration.Target`, parsed at write time (see section 1). Add a structured `bool IsKey` (name TBD at implementation time) to `MappingField` so:
- The mapping-profile save path can validate "at least one field is marked as key" without string-parsing a query option.
- The Angular wizard has a real field to bind a checkbox/radio to.
- The existing `?key=` write-time mechanism should be **derived from** this field (e.g. `ParseDestinationTarget`/`ParseTarget` stop reading a query-string option and instead the caller passes the key column name resolved from `mappingProfile.Fields.First(f => f.IsKey).TargetField`), not a second, independently-configured value that could drift out of sync with it.

### 2b. Constraint/index metadata in schema introspection
`DestinationColumnSchemaDto` ([DestinationSchemaDto.cs:37-42](../../src/FHIRBridge.Application/DTOs/DestinationSchemaDto.cs#L37)) currently carries `Name, DataType, MappingValueType, IsNullable, MaxLength` — no primary-key/unique-index/identity flag. `SqlDestinationSchemaService` ([SqlDestinationSchemaService.cs](../../src/FHIRBridge.Infrastructure/Destinations/SqlDestinationSchemaService.cs)) only queries `information_schema.columns` (`SqlServerColumnsSql` at lines 179-184; shared `InformationSchemaSql` for Postgres/MySQL at lines 22-27) — never `table_constraints`/`key_column_usage` or index catalogs. Add:
- `bool IsPrimaryKey` / `bool IsUnique` to `DestinationColumnSchemaDto`.
- For SQL Server: join against `sys.indexes`/`sys.index_columns` (or `INFORMATION_SCHEMA.TABLE_CONSTRAINTS` + `KEY_COLUMN_USAGE`, simpler and dialect-portable) alongside the existing column query.
- For Postgres/MySQL: join against `information_schema.table_constraints` + `information_schema.key_column_usage` filtered to `PRIMARY KEY`/`UNIQUE` constraint types.
- This is needed by: the Upsert-mode gate (section 3, item C.5) and the Upsert-strategy replacement (section 3, item A.4).

### 2c. "Source EHR/System Type" as a mappable virtual field
`MappedDestinationRecord` ([MappedDestinationRecord.cs](../../src/FHIRBridge.Application/DTOs/MappedDestinationRecord.cs)) carries `PipelineRunId, ResourceType, DestinationObject, SourceResourceId, Values, SourceJson` — nothing identifying which source connection/EHR vendor produced the record. Because a single `DestinationConfiguration` can already be shared across multiple `MappingProfile`s (each tied to exactly one `SourceConnectionId`), a destination table can legitimately receive rows from more than one EHR vendor (e.g. Epic + Healow + a flat-file feed all landing in the same warehouse table) with **no way to tell them apart** once written, since FHIRBridge no longer stamps any system column.

`SourceConnection.SourceSystemType` ([SourceConnection.cs:16,35](../../src/FHIRBridge.Domain/Entities/SourceConnection.cs#L16)) already holds exactly this vendor label (Epic, Healow, MEDITECH Greenfield, generic FHIR, HL7v2, flat file, DB, sample, webhook — the 9 source types from the root architecture doc). This value is **constant for every record a given `MappingProfile` produces** (one profile → one source connection), so it doesn't need JsonPath extraction like real FHIR fields — it's a lookup, not a transform.

Work needed:
- Expose "Source EHR/System Type" (and optionally the connection's `Name`, for customers with two connections of the same vendor type, e.g. two separate Epic instances for two hospitals) as a distinct, clearly-labeled **virtual field** in whatever list the mapping UI/backend uses to offer real FHIR JsonPath fields — not JsonPath-driven, just a constant resolved once per profile.
- Where `ConfiguredPipelineService` builds each `MappedDestinationRecord` (around `MapResourcesAsync`, [ConfiguredPipelineService.cs:493](../../src/FHIRBridge.Infrastructure/Pipeline/ConfiguredPipelineService.cs#L493)), resolve the owning `SourceConnection` (via `mappingProfile.SourceConnectionId`, already available in the loaded `ConfigurationSnapshot`) and supply its `SourceSystemType`/`Name` into the record's `Values` under whatever `TargetField` the admin mapped this virtual field to.
- This is a "nothing gets written unless explicitly mapped" feature, same as every other field — FHIRBridge does not auto-stamp this into any column; the admin must map it to one, and that mapping is validated as required (section 3, item C.5-new).

---

## 3. Backend plan

### A. Remove the DDL/creation behavior

1. **`SqlServerConnectionFactory.OpenConnectionAsync`** ([SqlServerConnectionFactory.cs:11-46](../../src/BuildingBlocks/FHIRBridge.Integration/Sql/SqlServerConnectionFactory.cs#L11)) — remove the `master`-connection + `CREATE DATABASE` block entirely (currently: builds a `master` connection string, opens it, runs `IF DB_ID(@DatabaseName) IS NULL BEGIN EXEC(N'CREATE DATABASE ...') END`). After removal, connecting simply opens the target connection string as given; a missing database surfaces as a normal SQL connection error. This factory is shared by the writer **and** by `SqlDestinationSchemaService` (`SqlDestinationSchemaService.cs:165`), so this single change also makes the schema-preview/test-connection path provably read-only.
2. **`MappedSqlServerDestinationWriter.EnsureTableAsync`** (`CREATE SCHEMA IF NOT EXISTS` + `CREATE TABLE IF NOT EXISTS`, lines 91-134) and **`RelationalDestinationWriterBase.EnsureTableAsync`** (lines 87-105) — remove the creation SQL entirely. Replace with an existence check (query `information_schema`/`sys.objects` for the table) that throws a clear, specific exception ("Destination table 'dbo.Patients' does not exist. Create it in your database before running this pipeline.") if missing. If the run-time pre-flight validation (section 3.D) already ran moments earlier in the same execution, this can trust that result instead of querying twice — decide at implementation time whether `EnsureTableAsync` becomes a no-op when pre-flight already passed, or stays as a defensive re-check.
3. **CDC mode's `EnsureCdcTableAsync`/`InsertCdcRecordAsync`** (`MappedSqlServerDestinationWriter.cs:218-283`) — ⚠ **Decision required before implementing.** This feature is currently only possible by auto-creating `{Table}_Cdc`, which can't survive under this model. Two options, pick one before writing code:
   - (a) Retire CDC mode entirely until redesigned around a customer-provisioned history table, or
   - (b) Require the customer to pre-create `{Table}_Cdc` to a documented shape, and have FHIRBridge validate-not-create it (same treatment as the main table).
4. **Upsert's delete-by-key + insert emulation** (`RelationalDestinationWriterBase.WriteAsync:60-68`, `DeleteByKeyAsync:140-156`) — once section 2b's constraint metadata exists, replace with a genuine `UPDATE ... WHERE <key> = @key` (INSERT if zero rows matched) or a native `MERGE`/`ON CONFLICT` per dialect — **only** when introspection confirms the key column has a real unique constraint/index. If it doesn't, Upsert mode must be refused with a clear validation error (see section 3.C.5) rather than silently emulated via delete+insert (risky under this model: customer-side foreign keys, triggers, or identity columns FHIRBridge can't see could be broken by a physical delete). This is the **highest-risk item in this entire plan** — it changes write semantics for any destination already configured with Upsert mode. Plan a migration/rollout note (e.g. detect existing Upsert-mode mapping profiles at deploy time and flag any whose key column lacks a unique constraint, so the customer can add one before the behavior change takes effect) before shipping this change.

### B. Extend schema introspection
Covered in section 2b above — add `IsPrimaryKey`/`IsUnique` to `DestinationColumnSchemaDto`, sourced via `information_schema.table_constraints`/`key_column_usage` (or SQL Server catalog views) alongside the existing column query in `SqlDestinationSchemaService`.

### B2. Expose "Source EHR/System Type" as a mappable field
Covered in section 2c above.

### C. Save-time validation (config-time gate)

Add a validation step inside `ConfigurationService.AddMappingProfileAsync` ([ConfigurationService.cs:244-260](../../src/FHIRBridge.Application/Services/ConfigurationService.cs#L244)) and `UpdateMappingProfileAsync` (lines 262-286), alongside the existing `EnsureSourceSupportsResourceTypeAsync` call (line 248 / 267) — that's the natural slot since `DestinationId`, `DestinationObject`, and `Fields` are all already in scope there, before anything persists. Requires injecting `IDestinationSchemaService` into `ConfigurationService` (not currently wired there). For relational destination types only (`SqlServer, AzureSql, PostgreSql, MySql` — check `DestinationConfiguration.DestinationType`, mirroring `IsRelational` in `SqlDestinationDataService.cs`/`SqlDestinationSchemaService.cs`), the check must:

1. Confirm `DestinationObject` resolves to an existing table (via `IDestinationSchemaService.GetSchemaAsync`).
2. Confirm every mapped `Field.TargetField` matches a real column on that table.
3. Confirm every NOT-NULL, no-default column on that table has a corresponding mapped field.
4. Confirm at least one field has `IsKey = true` (section 2a).
5. **Confirm at least one field is mapped to the "Source EHR/System Type" virtual field** (section 2c) — so a shared destination table stays traceable to the EHR/system each row came from.
6. If the configured write mode is Upsert, confirm the key column (`IsKey = true` field) carries a real unique constraint/index per the extended schema DTO (section 2b).
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
5. **Add an "IsKey" designation control** per mapping row (checkbox/radio) binding to the new `MappingField.IsKey` (section 2a) — required to be set on at least one row before Save. This replaces the current invisible `?key=` string mechanism with something the admin actually sees and sets.
6. **Add "Source EHR/System Type" as a selectable field** in the mapping step (section 2c), alongside the real FHIR JsonPath fields, showing the actual vendor label for the source connection this profile is attached to (not typed — a fixed value the admin drags onto a destination column, same interaction pattern as any other field). Block Save until it's mapped to some column, same treatment as the key requirement (item 5). Optionally pre-suggest this mapping when the destination table has an obviously-named column (`SourceSystem`, `EhrType`, etc.) as a convenience — but the admin must still confirm it; never auto-write it without an explicit mapping.
7. **Gate Upsert mode**: if the admin selects Upsert as the write mode, block Save until a key field is designated **and** the (extended) schema probe confirms that column carries a real unique constraint — with a clear message telling them to add one on their own database if it's missing, rather than FHIRBridge silently emulating it.
8. **Update "Test Connection" copy/tooltip** to state it performs a read-only schema check only, now that section 3.A.1 makes this true end-to-end.

---

## 5. Suggested sequencing

1. Remove DB/schema/table creation from the connection factory and both relational writers (section 3.A.1-2). Contained, highest-value fix, unblocks everything else being "safe" to build on top of. **No dependency on anything else in this plan.**
2. Extend schema introspection with constraint/uniqueness metadata (section 2b / 3.B).
3. Add `IsKey` to `MappingField` **and** the "Source EHR/System Type" virtual field (sections 2a, 2c) — land together; both are "new mapping-time concept the UI needs a control for and the backend needs to validate."
4. Add save-time validation in `ConfigurationService` (section 3.C) — depends on steps 2 and 3.
5. Add run-time pre-flight validation with caching in `ConfiguredPipelineService` (section 3.D) — depends on step 4 existing as reusable logic.
6. Update the Angular wizard (section 4) — depends on steps 2 and 3 (needs the new fields/DTOs to bind to).
7. Decide and implement CDC mode's fate (section 3.A.3, ⚠ decision required) and replace delete+insert Upsert with real UPDATE/MERGE gated on confirmed constraints (section 3.A.4) — **highest risk item, do last**, after everything else is stable, with its own rollout plan for existing Upsert-configured destinations.
8. Add the architecture-test DDL guardrail (section 3.E) — independent, do anytime, ideally early so it protects the rest of the work as it lands.

Steps 1 and 8 are low-risk and independent — do these first regardless of how the rest is sequenced. Steps 2–6 are interdependent and should be treated as one connected body of work. Step 7 is deliberately last.

---

## 6. Open decisions requiring a human answer before implementation

1. **CDC mode's fate** (section 3.A.3) — retire until redesigned, or require customer-provisioned companion table?
2. **Naming/shape of the new `MappingField.IsKey` property** — exact property name, and whether it lives on `MappingField` or is promoted to `MappingProfile` level (a profile-level "which target field is the key" pointer rather than a per-field boolean) — either works functionally; pick based on how the rest of the domain model reads.
3. **Whether "Source EHR/System Type" mapping is always required, or only required when a destination is shared across more than one source connection** — this plan currently specifies "always required" (simplest, most consistent rule); relaxing it to "only when shared" reduces friction for single-source destinations but adds a conditional check based on whether any other `MappingProfile` targets the same `DestinationId`. Confirm which behavior is wanted before implementing section 3.C.5.
4. **Upsert migration behavior** (section 3.A.4) — for destinations already configured with Upsert mode today (delete+insert emulation), decide whether the switch to real UPDATE/MERGE is a silent behavior change, a feature-flagged rollout, or requires detecting and blocking/warning on any existing Upsert mapping profile whose key column lacks a unique constraint before the new code path goes live.
