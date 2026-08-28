// ── Staged schema-op decision logic ─────────────────────────────────────────────
// Every "Create a new table…" / "Add column" click on the mapping canvas is staged as a PendingSchemaOp
// (see field-mapping-model.ts) rather than hitting the database immediately — the real DDL only runs when
// "Add to Pipeline"/"Update" flushes the queue (DestinationWizardComponent.flushPendingSchemaOps). Pulled
// out of field-mapping-canvas.component.ts/destination-wizard.component.ts into pure, DI-free functions —
// same convention as field-mapping-tree.util.ts — so the actual bug this file exists to fix (a table name
// that's only staged this session getting treated as "already exists", contradicting a live check that
// correctly says it doesn't) is unit-testable without Angular's TestBed or a live database.

import { Observable, concatMap, from, map, of, toArray } from 'rxjs';
import { PendingSchemaOp } from './field-mapping-model';

/** Just the slice of DestinationTable this file's functions need — kept minimal (not importing the real
 *  DestinationTable/DestinationColumn interfaces) so this stays a dependency-free pure-logic module. */
interface TableOriginInfo {
  fullName: string;
  origin?: 'probed' | 'userCreated' | 'restoredUnverified';
}

/**
 * Every table name any currently-queued op (create/add/drop/alter) still references — i.e. every table
 * whose existence this session cannot yet treat as fully confirmed, because *something* about it is still
 * waiting on a real database round trip. A createTable/addColumn op's own table is the common case (the
 * table may not be real yet); a dropColumn/alterColumn op's table is always already real (those actions
 * only ever target an already-known table), so including it here is harmless — it just means "don't worry,
 * this name is accounted for" either way.
 */
export function computePendingTableNames(ops: readonly PendingSchemaOp[]): Set<string> {
  return new Set(ops.map((op) => op.request.tableName));
}

/**
 * Table full-names this session can actually treat as "known to exist" — everything in `sqlTables()`
 * EXCEPT a table whose origin is 'restoredUnverified' (reconstructed from a previously-saved mapping on
 * reopen, not yet confirmed one way or the other by this session's own live re-probe — see
 * DestinationTable.origin's doc comment). This is the single choke point every other "does this table
 * exist" check in this file (and destination-wizard.component.ts's sqlTableOptions()) goes through, so a
 * stale cached table can never again be silently indistinguishable from a live-confirmed one — the bug
 * this whole module exists to fix (see this file's own header comment).
 *
 * Deliberately still INCLUDES a table with origin 'userCreated' whether or not its create/add-column has
 * actually flushed yet — same as before this function existed, callers that need "confirmed for real,
 * not just staged this session" subtract pendingTableNames too (see isConfirmedRealTable).
 */
export function confirmedTableNames(tables: readonly TableOriginInfo[]): string[] {
  return tables.filter((t) => t.origin !== 'restoredUnverified').map((t) => t.fullName);
}

/** confirmedTableNames' slice, plus the bare table name a candidate can also match against. Exported so
 *  callers that need to pass real table data through to findConfirmedTableMatch/isConfirmedRealTable (e.g.
 *  FieldMappingCanvasComponent's own `sqlTables` input) can name the shape precisely, rather than widening
 *  to the full DestinationTable/DestinationColumn interfaces this file otherwise deliberately avoids
 *  importing (see this file's own header comment). */
export interface TableIdentityInfo extends TableOriginInfo {
  tableName: string;
}

/**
 * Finds a table this session can treat as genuinely CONFIRMED to exist (same "not restoredUnverified" rule
 * as confirmedTableNames — a table only just restored from a saved mapping, not yet re-verified, must never
 * silently auto-attach either) whose identity matches `candidate` — a resource's conventional default name,
 * re-qualified per destType (see qualify()'s own doc comment).
 *
 * Checked against BOTH the table's schema/database-qualified fullName AND its bare tableName alone,
 * case-insensitively, because the live schema probe qualifies a table's fullName differently per engine:
 * SQL Server's is "dbo.Patient" (which qualify() already predicts, since "dbo" is its one, well-known
 * default schema), but MySQL's is "{the connected DATABASE's own name}.Patient" — a value qualify() has no
 * way to know in advance — and PostgreSQL's is "public.Patient" (its real default schema, which qualify()
 * never adds since it only ever prefixes SQL Server's "dbo."). Matching the bare tableName too is what lets
 * MySQL/PostgreSQL recognize a real existing table by name at all, regardless of what its live-probed
 * fullName turns out to be qualified with. Mirrors the exact same fullName-or-tableName fallback
 * DestinationWizardComponent's own _resolveDestinationObjectForCanvas already uses for the analogous
 * "does this name refer to a table we already know about" question when restoring a Mapping Profile.
 */
export function findConfirmedTableMatch(
  candidate: string,
  tables: readonly TableIdentityInfo[],
): TableIdentityInfo | null {
  const lower = candidate.toLowerCase();
  return (
    tables.find(
      (t) =>
        t.origin !== 'restoredUnverified' &&
        (t.fullName.toLowerCase() === lower || t.tableName.toLowerCase() === lower),
    ) ?? null
  );
}

/** The two states a target card's table must never conflate — see tableTargetStatus below and
 *  field-mapping-target-card.component.ts's own tableStatus computed, which renders this as a badge.
 *  There used to be a third state, 'suggested' — a DEST_RESOURCE_DEFS default guess (e.g. "dbo.Patient")
 *  shown as if it were a real table before anything had confirmed it existed. That guess is gone: a
 *  resource's target is now either empty (no card renders at all — see
 *  FieldMappingCanvasComponent.isPrimaryTargetValid and DestinationWizardComponent._rebuildRows, neither
 *  of which will ever hand this function an unconfirmed, non-pending name any more) or one of these two
 *  real states. */
export type TableTargetStatus = 'pending' | 'confirmed';

/** One row's worth of "what should the target card show" — 'pending' while an unflushed queued op still
 *  references this name (a "Create a new table…"/"Add column" staged this session but not yet flushed),
 *  'confirmed' once nothing's left queued against it. Every caller only ever invokes this for a name
 *  that's already known to genuinely be one or the other — a real, already-probed table (extra tables are
 *  always this, see FieldMappingTargetCardComponent.tableStatus) or a table this session just staged
 *  creating (see isPrimaryTargetValid/canQueueAddColumn) — never an unconfirmed guess with nothing behind
 *  it at all (see this type's own doc comment for why that third state no longer exists). */
export function tableTargetStatus(
  name: string,
  pendingTableNames: ReadonlySet<string>,
): TableTargetStatus {
  return pendingTableNames.has(name) ? 'pending' : 'confirmed';
}

/**
 * What a live schema re-probe on reopen (DestinationWizardComponent._refreshSqlTablesFromLiveSchema)
 * should do with its result — extracted as a pure function so the "don't silently swallow a failed
 * verification" behavior is unit-testable without a live database or Angular's TestBed. `result: null`
 * covers the ad-hoc-probe fallback path having no credentials to even attempt a probe with (a brand-new,
 * not-yet-saved SQL node reopened before its password is available) — treated the same as a real failure,
 * since either way nothing here got verified and the caller must not pretend otherwise.
 */
export function describeSchemaVerificationOutcome(
  result: { connected: boolean; error: string | null } | null,
): { state: 'verified' | 'failed'; warning: string | null } {
  if (result === null) {
    return {
      state: 'failed',
      warning: 'Could not verify the destination schema — showing the last-saved mapping. Some tables may no longer exist; reconnect to confirm before saving.',
    };
  }
  if (!result.connected) {
    return {
      state: 'failed',
      warning: result.error ?? 'Could not verify the destination schema against the live database — showing the last-saved mapping. Some tables may no longer exist; reconnect to confirm before saving.',
    };
  }
  return { state: 'verified', warning: null };
}

/**
 * True only when `name` is CONFIRMED to exist right now — matches a known table (findConfirmedTableMatch —
 * fullName OR bare tableName, case-insensitively, never a 'restoredUnverified' one) and isn't merely staged
 * there by an unflushed op this session. `sqlTables` mixes two things by construction (see
 * onColumnAdded/onTableCreated in destination-wizard.component.ts, which upsert a table into the same list
 * the moment a canvas action is submitted, long before any real DDL runs): tables the initial connection
 * probe actually found, and tables/columns optimistically previewed from a canvas action that's still only
 * queued. Subtracting `pendingTableNames` is what tells those two apart — a name that's ONLY there because
 * of a still-pending op is not yet a real duplicate.
 */
export function isConfirmedRealTable(
  name: string,
  sqlTables: readonly TableIdentityInfo[],
  pendingTableNames: ReadonlySet<string>,
): boolean {
  return findConfirmedTableMatch(name, sqlTables) !== null && !pendingTableNames.has(name);
}

/**
 * Whether an EXTRA table's card should still render at all — an extra table used to be assumed real
 * unconditionally (FieldMappingTargetCardComponent.tableStatus's old `isExtra() ? 'confirmed' : ...`),
 * on the theory that extraTablesByGroup only ever gains an entry via "+ Add a table from your
 * database…" (onAddExtraTable), which does only ever offer an already-probed, real name. But
 * extraTablesByGroup can also be populated straight from a restored dest_mapping_summary_v1 with no
 * live check at all (see applyMappingSummaryDocument in field-mapping-summary.model.ts), so a
 * restored extra-table reference whose table has since been dropped can reach this component too.
 *
 * Mirrors canQueueAddColumn's own "confirmed or pending" allowance for the primary target — true for
 * a genuinely real table (findConfirmedTableMatch, the same fullName-or-bare-tableName,
 * case-insensitive, restoredUnverified-excluding match every other "does this table really exist"
 * question in this file already goes through) OR one still staged this session
 * (pendingTableNames), so a brand-new extra table created via "Create a new table…" still renders
 * (and shows PENDING, then CONFIRMED — see tableTargetStatus) exactly as before this function
 * existed. False means genuinely neither — see FieldMappingCanvasComponent.targetCards(), which
 * excludes such a card entirely rather than ever showing it as CONFIRMED.
 *
 * Deliberately takes the raw TableIdentityInfo[] (sqlTables), not the already-flattened
 * sqlTableOptions string[]: a restored reference may still be its original bare tableName (e.g.
 * "Patient_test") rather than the live-qualified fullName MySQL/PostgreSQL actually probe
 * ("fhirbridge_output.Patient_test"/"public.Patient_test") if reconcileRestoredTablesWithLiveSchema
 * hasn't (yet, or ever) rewritten it — findConfirmedTableMatch's tableName fallback is what still
 * recognizes it as real either way, so this doesn't depend on that rewrite having already run.
 */
export function isExtraTableCardValid(
  tableName: string,
  sqlTables: readonly TableIdentityInfo[],
  pendingTableNames: ReadonlySet<string>,
): boolean {
  return findConfirmedTableMatch(tableName, sqlTables) !== null || pendingTableNames.has(tableName);
}

/**
 * Whether "Add column" may proceed against `tableName` right now — either it's a real, already-existing
 * table, or a "Create a new table…" for it is already staged this session (so the real CREATE will run
 * immediately before this ADD COLUMN when the queue flushes — see runQueuedOpsSequentially). False means
 * this table has never been created and nothing is staged to create it either: queuing an add-column
 * against it would only ever fail once flushed (SqlDestinationSchemaService deliberately never
 * auto-creates a table for a column add — see AddColumnAsync's own doc comment), so the canvas should
 * refuse it up front with a clear message instead of accepting it now and failing silently later.
 */
export function canQueueAddColumn(
  tableName: string,
  sqlTableOptions: readonly string[],
  pendingTableNames: ReadonlySet<string>,
): boolean {
  return sqlTableOptions.includes(tableName) || pendingTableNames.has(tableName);
}

/** Shared wording for "this exact table already exists" — used both by the fast local check
 *  (describeCreateTableConflict) and the live one (describeLiveCreateTableConflict) below, so a table
 *  caught by either path reads identically to the user regardless of which one caught it. */
function alreadyExistsMessage(name: string): string {
  return `Table '${name}' already exists. Please choose a different table name.`;
}

/**
 * Decides whether "Create a new table…" for `name` should be blocked, and with what message — or `null`
 * to let it proceed (in which case the caller still owes it a LIVE check — see describeLiveCreateTableConflict
 * — before actually queueing anything, since a real table this canvas simply never learned about, e.g. the
 * connection was probed before the table existed, would otherwise slip through as a false negative here).
 * Three outcomes:
 *  - already staged this session (a createTable/addColumn op already references this exact name, not yet
 *    flushed): block with a distinct message pointing at what to do instead, and don't queue a duplicate.
 *  - confirmed to already exist for real (`isConfirmedRealTable` — findConfirmedTableMatch under the hood,
 *    so MySQL/PostgreSQL recognize their own live-probed "{database}.Patient"/"public.Patient" against a
 *    bare "Patient" candidate exactly like the live check does), or explicitly added onto the canvas as a
 *    real extra table (`extraTables` — only ever populated with real, already-probed names via "+ Add a
 *    table from your database…", see onAddExtraTable): block with the ordinary "already exists" message.
 *  - anything else — including a resource's own still-unfulfilled GUESSED target name, which was never a
 *    real duplicate in the first place — proceeds (to the live check).
 */
export function describeCreateTableConflict(
  name: string,
  context: {
    extraTables: readonly string[];
    sqlTables: readonly TableIdentityInfo[];
    pendingTableNames: ReadonlySet<string>;
  },
): string | null {
  if (context.pendingTableNames.has(name)) {
    return `${name} is already staged in this session — click "Add to Pipeline" to finish creating it, or map/add columns onto it directly.`;
  }
  if (context.extraTables.includes(name) || isConfirmedRealTable(name, context.sqlTables, context.pendingTableNames)) {
    return alreadyExistsMessage(name);
  }
  return null;
}

/**
 * The live counterpart to describeCreateTableConflict — call this ONLY once the local, synchronous check
 * above has already returned `null` (nothing already known locally), against the response of a fresh
 * DestinationSchemaService.probe(connection) round trip. Catches the case the local check structurally
 * cannot: a table that genuinely already exists in the real database but this canvas's local
 * `sqlTableOptions` never learned about (never probed, or probed before the table existed) — the exact
 * gap that let a duplicate CREATE TABLE reach the backend and fail only once "Add to Pipeline" flushed it.
 * A probe that couldn't even connect is also treated as a block (returning its own error) rather than
 * optimistically letting creation through unverified.
 *
 * Duplicate detection is findConfirmedTableMatch — the exact same "MySQL/PostgreSQL existing-table
 * detection" fix already applied to the auto-attach path (DestinationWizardComponent._rebuildRows), for
 * the identical reason: a live probe's fullName is SQL Server's "dbo.Patient" (which `name` already
 * matches directly, since qualify() predicts it), but MySQL's is "{the connected database's own
 * name}.Patient" and PostgreSQL's is "public.Patient" — neither of which `name` (bare, for those two
 * engines) would ever match on fullName alone. Matching tableName too — findConfirmedTableMatch's own
 * doc comment has the full reasoning — is what lets MySQL/PostgreSQL catch a genuine duplicate here instead
 * of letting it through to fail later at flush time.
 */
export function describeLiveCreateTableConflict(
  name: string,
  probe: { connected: boolean; error: string | null; tables: readonly TableIdentityInfo[] },
): string | null {
  if (!probe.connected) {
    return probe.error ?? `Could not verify "${name}" against the destination database — try again.`;
  }
  return findConfirmedTableMatch(name, probe.tables) ? alreadyExistsMessage(name) : null;
}

/**
 * Runs each item's async operation strictly in order — the next item never starts until the previous one
 * has actually completed — stopping at the first failure. `onItemSucceeded` fires only for an item that
 * really completed, immediately after it does and before the next item starts, so a caller removing
 * completed ops from its own queue (see flushPendingSchemaOps) never drops one a later item's failure
 * then leaves stuck mid-flight. The failing item and everything still behind it are left untouched by
 * this function entirely — that's what guarantees a later step that depends on an earlier one (e.g. an
 * ADD COLUMN queued against a table a CREATE TABLE just before it was supposed to create) can never run
 * after its dependency failed.
 */
export function runQueuedOpsSequentially<T>(
  items: readonly T[],
  run: (item: T) => Observable<void>,
  onItemSucceeded: (item: T) => void,
): Observable<boolean> {
  if (items.length === 0) return of(true);
  return from(items).pipe(
    concatMap((item) =>
      run(item).pipe(
        map(() => {
          onItemSucceeded(item);
          return true;
        }),
      ),
    ),
    toArray(),
    map((results) => results.every(Boolean)),
  );
}
