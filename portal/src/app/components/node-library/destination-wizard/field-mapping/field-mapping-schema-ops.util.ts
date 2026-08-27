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
 * True only when `name` is CONFIRMED to exist right now — present in the canvas's known table names
 * (`sqlTableOptions`) and not merely staged there by an unflushed op this session. `sqlTableOptions` mixes
 * two things by construction (see onColumnAdded/onTableCreated in destination-wizard.component.ts, which
 * upsert a table into the same list the moment a canvas action is submitted, long before any real DDL
 * runs): tables the initial connection probe actually found, and tables/columns optimistically previewed
 * from a canvas action that's still only queued. Subtracting `pendingTableNames` is what tells those two
 * apart — a name that's ONLY there because of a still-pending op is not yet a real duplicate.
 */
export function isConfirmedRealTable(
  name: string,
  sqlTableOptions: readonly string[],
  pendingTableNames: ReadonlySet<string>,
): boolean {
  return sqlTableOptions.includes(name) && !pendingTableNames.has(name);
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
 *  - confirmed to already exist for real (`isConfirmedRealTable`), or explicitly added onto the canvas as
 *    a real extra table (`extraTables` — only ever populated with real, already-probed names via "+ Add a
 *    table from your database…", see onAddExtraTable): block with the ordinary "already exists" message.
 *  - anything else — including a resource's own still-unfulfilled GUESSED target name, which was never a
 *    real duplicate in the first place — proceeds (to the live check).
 */
export function describeCreateTableConflict(
  name: string,
  context: {
    extraTables: readonly string[];
    sqlTableOptions: readonly string[];
    pendingTableNames: ReadonlySet<string>;
  },
): string | null {
  if (context.pendingTableNames.has(name)) {
    return `${name} is already staged in this session — click "Add to Pipeline" to finish creating it, or map/add columns onto it directly.`;
  }
  if (context.extraTables.includes(name) || isConfirmedRealTable(name, context.sqlTableOptions, context.pendingTableNames)) {
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
 */
export function describeLiveCreateTableConflict(
  name: string,
  probe: { connected: boolean; error: string | null; tables: readonly { fullName: string }[] },
): string | null {
  if (!probe.connected) {
    return probe.error ?? `Could not verify "${name}" against the destination database — try again.`;
  }
  // Case-insensitive on purpose — SQL Server's default collation treats "dbo.Patient" and "dbo.patient"
  // as the SAME object (SqlDestinationSchemaService.TableExistsAsync's OBJECT_ID lookup relies on exactly
  // this), so a differently-cased match here must still block: letting it through would just move the
  // "already exists" failure back to flush time, reproducing the bug this whole check exists to fix.
  const lower = name.toLowerCase();
  return probe.tables.some((t) => t.fullName.toLowerCase() === lower) ? alreadyExistsMessage(name) : null;
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
