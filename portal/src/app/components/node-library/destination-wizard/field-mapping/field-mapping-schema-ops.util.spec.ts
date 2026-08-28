import { Observable, of, throwError } from 'rxjs';
import { AddColumnRequest, CreateTableRequest, DestinationProbeRequest, DestinationTable } from '../../../../services/destination-schema.service';
import { PendingSchemaOp } from './field-mapping-model';
import {
  canQueueAddColumn, computePendingTableNames, confirmedTableNames, describeCreateTableConflict,
  describeLiveCreateTableConflict, describeSchemaVerificationOutcome, findConfirmedTableMatch,
  isConfirmedRealTable, isExtraTableCardValid, runQueuedOpsSequentially, tableTargetStatus,
} from './field-mapping-schema-ops.util';

const CONNECTION: DestinationProbeRequest = { destinationType: 'sqlserver' };

function createTableOp(tableName: string): PendingSchemaOp {
  const request: CreateTableRequest = { connection: CONNECTION, tableName };
  return { kind: 'createTable', request };
}

function addColumnOp(tableName: string, columnName = 'Notes'): PendingSchemaOp {
  const request: AddColumnRequest = { connection: CONNECTION, tableName, columnName, dataType: 'nvarchar(50)' };
  return { kind: 'addColumn', request };
}

// The live schema probe qualifies a table's fullName differently per engine — SQL Server "dbo.Patient",
// MySQL "{connected database}.Patient", PostgreSQL "public.Patient" — while a resource's default candidate
// is always bare ("Patient") for MySQL/PostgreSQL (qualify() only ever prefixes SQL Server's "dbo."). This
// constructs a DestinationTable literal with fullName and tableName set independently (rather than the
// `table()` helper further below, which assumes fullName always splits cleanly on '.') specifically so they
// can diverge exactly the way each real engine's probe response does. Shared by every describe block below
// that exercises MySQL/PostgreSQL existing-table detection (isConfirmedRealTable, describeCreateTableConflict,
// describeLiveCreateTableConflict, findConfirmedTableMatch).
function liveTable(
  fullName: string,
  tableName: string,
  origin?: 'probed' | 'userCreated' | 'restoredUnverified',
): DestinationTable {
  return { schemaName: fullName.includes('.') ? fullName.slice(0, fullName.lastIndexOf('.')) : '', tableName, fullName, columns: [], origin };
}

describe('computePendingTableNames', () => {
  it('is empty when there are no pending ops (scenario F)', () => {
    expect(computePendingTableNames([])).toEqual(new Set());
  });

  it('collects the table name of every queued op regardless of kind', () => {
    const names = computePendingTableNames([createTableOp('dbo.Patient'), addColumnOp('dbo.Patient'), addColumnOp('dbo.Other')]);
    expect(names).toEqual(new Set(['dbo.Patient', 'dbo.Other']));
  });
});

describe('isConfirmedRealTable', () => {
  it('SQL Server: is true for a table from the live probe with nothing pending against it (scenario A)', () => {
    expect(isConfirmedRealTable('dbo.Patient', [liveTable('dbo.Patient', 'Patient', 'probed')], new Set())).toBeTrue();
  });

  it('is false for a table that is only known because of a still-pending op (scenario B)', () => {
    // sqlTables already lists it (optimistic preview applied the moment the create was queued — see
    // onTableCreated), but its createTable op hasn't flushed yet.
    const pending = new Set(['dbo.Patient']);
    expect(isConfirmedRealTable('dbo.Patient', [liveTable('dbo.Patient', 'Patient', 'userCreated')], pending)).toBeFalse();
  });

  it('is false for a name absent from the known tables entirely (scenario D — DB table was deleted)', () => {
    expect(isConfirmedRealTable('dbo.Patient', [], new Set())).toBeFalse();
  });

  it('MySQL: is true when the confirmed table is "my_database.Patient" and the candidate is bare "Patient"', () => {
    expect(isConfirmedRealTable('Patient', [liveTable('my_database.Patient', 'Patient', 'probed')], new Set())).toBeTrue();
  });

  it('PostgreSQL: is true when the confirmed table is "public.Patient" and the candidate is bare "Patient"', () => {
    expect(isConfirmedRealTable('Patient', [liveTable('public.Patient', 'Patient', 'probed')], new Set())).toBeTrue();
  });

  it('matches case-insensitively (SQL Server fullName, MySQL/PostgreSQL tableName)', () => {
    expect(isConfirmedRealTable('DBO.PATIENT', [liveTable('dbo.Patient', 'Patient', 'probed')], new Set())).toBeTrue();
    expect(isConfirmedRealTable('patient', [liveTable('my_database.PATIENT', 'PATIENT', 'probed')], new Set())).toBeTrue();
  });

  it('EXCLUDES a "restoredUnverified" table — must never be treated as confirmed', () => {
    expect(isConfirmedRealTable('Patient', [liveTable('my_database.Patient', 'Patient', 'restoredUnverified')], new Set())).toBeFalse();
  });
});

// ── isExtraTableCardValid (extra-table CONFIRMED-badge fix — a restored, no-longer-real extra table
// (e.g. "Patient_test" restored from dest_mapping_summary_v1 via applyMappingSummaryDocument, with no
// live check at all) must never render as CONFIRMED just because extraTablesByGroup still names it) ──
describe('isExtraTableCardValid', () => {
  it('SQL Server: a live extra table is valid (scenario A)', () => {
    expect(isExtraTableCardValid('dbo.Patient_test', [liveTable('dbo.Patient_test', 'Patient_test', 'probed')], new Set())).toBeTrue();
  });

  it('SQL Server: a restored extra table that still exists live is valid (scenario B)', () => {
    expect(isExtraTableCardValid('dbo.Patient_test', [liveTable('dbo.Patient_test', 'Patient_test', 'probed')], new Set())).toBeTrue();
  });

  it('SQL Server: a restored extra table that no longer exists live is NOT valid (scenario C — the reported bug)', () => {
    expect(isExtraTableCardValid('dbo.Patient_test', [liveTable('dbo.Patient', 'Patient', 'probed')], new Set())).toBeFalse();
  });

  it('MySQL: a live extra table probed as "{database}.Patient_test" is valid for the bare restored candidate (scenario D)', () => {
    expect(isExtraTableCardValid('Patient_test', [liveTable('fhirbridge_output.Patient_test', 'Patient_test', 'probed')], new Set())).toBeTrue();
  });

  it('MySQL: a restored extra table that no longer exists live is NOT valid (scenario E)', () => {
    expect(isExtraTableCardValid('Patient_test', [liveTable('fhirbridge_output.Patient', 'Patient', 'probed')], new Set())).toBeFalse();
  });

  it('PostgreSQL: a live extra table probed as "public.Patient_test" is valid for the bare restored candidate (scenario F)', () => {
    expect(isExtraTableCardValid('Patient_test', [liveTable('public.Patient_test', 'Patient_test', 'probed')], new Set())).toBeTrue();
  });

  it('PostgreSQL: a restored extra table that no longer exists live is NOT valid (scenario G)', () => {
    expect(isExtraTableCardValid('Patient_test', [liveTable('public.Patient', 'Patient', 'probed')], new Set())).toBeFalse();
  });

  it('matches case-insensitively (scenario H)', () => {
    expect(isExtraTableCardValid('PATIENT_TEST', [liveTable('fhirbridge_output.Patient_test', 'Patient_test', 'probed')], new Set())).toBeTrue();
  });

  it('EXCLUDES a "restoredUnverified" table — must never be treated as confirmed on its own (scenario I)', () => {
    expect(isExtraTableCardValid('Patient_test', [liveTable('fhirbridge_output.Patient_test', 'Patient_test', 'restoredUnverified')], new Set())).toBeFalse();
  });

  it('a brand-new extra table only staged this session (not yet flushed) stays valid — preserves the PENDING lifecycle (scenario J)', () => {
    expect(isExtraTableCardValid('dbo.NewExtra', [], new Set(['dbo.NewExtra']))).toBeTrue();
  });

  it('is false when sqlTables is empty and nothing is pending (no data to confirm against)', () => {
    expect(isExtraTableCardValid('dbo.Patient_test', [], new Set())).toBeFalse();
  });
});

describe('canQueueAddColumn', () => {
  it('allows a column on an existing real table (scenario A)', () => {
    expect(canQueueAddColumn('dbo.Patient', ['dbo.Patient'], new Set())).toBeTrue();
  });

  it('allows a column on a table with a pending create queued (scenario B)', () => {
    expect(canQueueAddColumn('dbo.Patient', ['dbo.Patient'], new Set(['dbo.Patient']))).toBeTrue();
  });

  it('refuses a column on a table that is neither real nor pending', () => {
    expect(canQueueAddColumn('dbo.Ghost', [], new Set())).toBeFalse();
  });
});

describe('describeCreateTableConflict', () => {
  it('allows creating a brand-new table (no conflict)', () => {
    expect(describeCreateTableConflict('dbo.Patient', {
      extraTables: [], sqlTables: [], pendingTableNames: new Set(),
    })).toBeNull();
  });

  it('blocks re-creating a table that already has a pending create staged, with a distinct message (the core fix)', () => {
    const msg = describeCreateTableConflict('dbo.Patient', {
      extraTables: [], sqlTables: [liveTable('dbo.Patient', 'Patient', 'userCreated')], pendingTableNames: new Set(['dbo.Patient']),
    });
    expect(msg).toContain('already staged in this session');
  });

  it('SQL Server: blocks creating a table confirmed to already exist for real, with the required exact wording', () => {
    const msg = describeCreateTableConflict('dbo.Patient', {
      extraTables: [], sqlTables: [liveTable('dbo.Patient', 'Patient', 'probed')], pendingTableNames: new Set(),
    });
    expect(msg).toBe("Table 'dbo.Patient' already exists. Please choose a different table name.");
  });

  it('MySQL: blocks creating "Patient" when the confirmed table is "my_database.Patient"', () => {
    const msg = describeCreateTableConflict('Patient', {
      extraTables: [], sqlTables: [liveTable('my_database.Patient', 'Patient', 'probed')], pendingTableNames: new Set(),
    });
    expect(msg).toBe("Table 'Patient' already exists. Please choose a different table name.");
  });

  it('PostgreSQL: blocks creating "Patient" when the confirmed table is "public.Patient"', () => {
    const msg = describeCreateTableConflict('Patient', {
      extraTables: [], sqlTables: [liveTable('public.Patient', 'Patient', 'probed')], pendingTableNames: new Set(),
    });
    expect(msg).toBe("Table 'Patient' already exists. Please choose a different table name.");
  });

  it('MySQL/PostgreSQL: allows creating "Patient" when no table with that name actually exists', () => {
    expect(describeCreateTableConflict('Patient', {
      extraTables: [], sqlTables: [liveTable('my_database.Other', 'Other', 'probed')], pendingTableNames: new Set(),
    })).toBeNull();
    expect(describeCreateTableConflict('Patient', {
      extraTables: [], sqlTables: [liveTable('public.Other', 'Other', 'probed')], pendingTableNames: new Set(),
    })).toBeNull();
  });

  it('matches case-insensitively for all three engines', () => {
    expect(describeCreateTableConflict('DBO.PATIENT', {
      extraTables: [], sqlTables: [liveTable('dbo.Patient', 'Patient', 'probed')], pendingTableNames: new Set(),
    })).not.toBeNull();
    expect(describeCreateTableConflict('patient', {
      extraTables: [], sqlTables: [liveTable('my_database.PATIENT', 'PATIENT', 'probed')], pendingTableNames: new Set(),
    })).not.toBeNull();
    expect(describeCreateTableConflict('PATIENT', {
      extraTables: [], sqlTables: [liveTable('PUBLIC.patient', 'patient', 'probed')], pendingTableNames: new Set(),
    })).not.toBeNull();
  });

  it('does NOT block on a "restoredUnverified" table — no fake/suggested table blocks a genuine create', () => {
    const msg = describeCreateTableConflict('Patient', {
      extraTables: [], sqlTables: [liveTable('my_database.Patient', 'Patient', 'restoredUnverified')], pendingTableNames: new Set(),
    });
    expect(msg).toBeNull();
  });

  it('blocks re-adding a real table already on the canvas as an extra table', () => {
    const msg = describeCreateTableConflict('dbo.PatientContact', {
      extraTables: ['dbo.PatientContact'], sqlTables: [], pendingTableNames: new Set(),
    });
    expect(msg).toBe("Table 'dbo.PatientContact' already exists. Please choose a different table name.");
  });

  it('does NOT block a resource\'s own unfulfilled guessed target — this is the exact bug being fixed', () => {
    // Before the fix: a resource's default-guessed target (e.g. "dbo.Patient", never actually created)
    // made isPrimaryTargetValid/targetAlreadyReal true purely because the name matched, contradicting
    // AddColumnAsync's own live "the table does not exist" check. Neither extraTables nor sqlTables
    // knows about this name at all here, so there is genuinely nothing to conflict with.
    const msg = describeCreateTableConflict('dbo.Patient', {
      extraTables: [], sqlTables: [], pendingTableNames: new Set(),
    });
    expect(msg).toBeNull();
  });
});

describe('describeLiveCreateTableConflict', () => {
  it('SQL Server: blocks a table the live probe finds — the exact bug reported: a real DB table the local cache never learned about (scenario 1 / D)', () => {
    const msg = describeLiveCreateTableConflict('dbo.Patient', {
      connected: true, error: null,
      tables: [liveTable('dbo.Patient', 'Patient', 'probed'), liveTable('dbo.Other', 'Other', 'probed')],
    });
    expect(msg).toBe("Table 'dbo.Patient' already exists. Please choose a different table name.");
  });

  it('SQL Server: allows a table the live probe genuinely does not find (scenario 2)', () => {
    const msg = describeLiveCreateTableConflict('dbo.TestPatient', {
      connected: true, error: null, tables: [liveTable('dbo.Patient', 'Patient', 'probed')],
    });
    expect(msg).toBeNull();
  });

  it('MySQL: detects an existing "Patient" table even though the live schema returns "my_database.Patient"', () => {
    const msg = describeLiveCreateTableConflict('Patient', {
      connected: true, error: null, tables: [liveTable('my_database.Patient', 'Patient', 'probed')],
    });
    expect(msg).toBe("Table 'Patient' already exists. Please choose a different table name.");
  });

  it('MySQL: allows creation when no table with this name actually exists', () => {
    const msg = describeLiveCreateTableConflict('Patient', {
      connected: true, error: null, tables: [liveTable('my_database.Other', 'Other', 'probed')],
    });
    expect(msg).toBeNull();
  });

  it('PostgreSQL: detects an existing "Patient" table even though the live schema returns "public.Patient"', () => {
    const msg = describeLiveCreateTableConflict('Patient', {
      connected: true, error: null, tables: [liveTable('public.Patient', 'Patient', 'probed')],
    });
    expect(msg).toBe("Table 'Patient' already exists. Please choose a different table name.");
  });

  it('PostgreSQL: allows creation when no table with this name actually exists', () => {
    const msg = describeLiveCreateTableConflict('Patient', {
      connected: true, error: null, tables: [liveTable('public.Other', 'Other', 'probed')],
    });
    expect(msg).toBeNull();
  });

  it('matches case-insensitively for all three engines — SQL Server via fullName, MySQL/PostgreSQL via tableName (scenario 6)', () => {
    // SqlDestinationSchemaService.TableExistsAsync's OBJECT_ID lookup treats "dbo.Patient" and
    // "dbo.patient" as the same real object — this check must agree, or a differently-cased duplicate
    // would slip past validation here only to fail for real once "Add to Pipeline" flushes it.
    const sqlServerProbe = { connected: true, error: null, tables: [liveTable('dbo.Patient', 'Patient', 'probed')] };
    expect(describeLiveCreateTableConflict('dbo.Patient', sqlServerProbe)).not.toBeNull();
    expect(describeLiveCreateTableConflict('dbo.patient', sqlServerProbe)).not.toBeNull();
    expect(describeLiveCreateTableConflict('DBO.PATIENT', sqlServerProbe)).not.toBeNull();

    const mysqlProbe = { connected: true, error: null, tables: [liveTable('my_database.PATIENT', 'PATIENT', 'probed')] };
    expect(describeLiveCreateTableConflict('patient', mysqlProbe)).not.toBeNull();
    expect(describeLiveCreateTableConflict('Patient', mysqlProbe)).not.toBeNull();

    const postgresProbe = { connected: true, error: null, tables: [liveTable('PUBLIC.patient', 'patient', 'probed')] };
    expect(describeLiveCreateTableConflict('PATIENT', postgresProbe)).not.toBeNull();
  });

  it('does NOT treat a "restoredUnverified" table as a conflict — no fake/suggested table blocks a genuine create', () => {
    // Not expected in practice (a live probe() response is never client-tagged 'restoredUnverified' — see
    // DestinationColumn.origin's own doc comment), but findConfirmedTableMatch's exclusion must still hold
    // here defensively, exactly as it does for the auto-attach path.
    const msg = describeLiveCreateTableConflict('Patient', {
      connected: true, error: null, tables: [liveTable('my_database.Patient', 'Patient', 'restoredUnverified')],
    });
    expect(msg).toBeNull();
  });

  it('blocks (rather than optimistically allowing) when the live probe could not even connect', () => {
    const msg = describeLiveCreateTableConflict('dbo.Patient', { connected: false, error: 'Login failed.', tables: [] });
    expect(msg).toBe('Login failed.');
  });

  it('falls back to a generic message when a failed probe carries no error text', () => {
    const msg = describeLiveCreateTableConflict('dbo.Patient', { connected: false, error: null, tables: [] });
    expect(msg).toContain('Could not verify');
  });
});

describe('runQueuedOpsSequentially', () => {
  it('resolves true immediately for an empty queue (scenario F)', (done) => {
    const succeeded: number[] = [];
    runQueuedOpsSequentially<number>([], () => of(undefined), (i) => succeeded.push(i)).subscribe((ok) => {
      expect(ok).toBeTrue();
      expect(succeeded).toEqual([]);
      done();
    });
  });

  it('runs every item in order and reports each success before the next starts (scenario C — multiple columns)', (done) => {
    const started: number[] = [];
    const succeeded: number[] = [];
    const run = (i: number): Observable<void> => {
      started.push(i);
      return of(undefined);
    };
    runQueuedOpsSequentially<number>([1, 2, 3], run, (i) => succeeded.push(i)).subscribe((ok) => {
      expect(ok).toBeTrue();
      expect(started).toEqual([1, 2, 3]);
      expect(succeeded).toEqual([1, 2, 3]);
      done();
    });
  });

  it('stops at the first failure — nothing after it runs, and the failed/remaining items are never reported as succeeded (scenario E)', (done) => {
    const started: string[] = [];
    const succeeded: string[] = [];
    const run = (op: string): Observable<void> => {
      started.push(op);
      return op === 'createTable' ? throwError(() => new Error('Create table "dbo.Patient" failed: already exists')) : of(undefined);
    };
    runQueuedOpsSequentially<string>(['createTable', 'addColumn1', 'addColumn2'], run, (op) => succeeded.push(op)).subscribe({
      next: () => fail('should not emit a next value — the sequence errors out'),
      error: (err) => {
        expect(err.message).toContain('Create table');
        // Only the failing op was ever started — the two addColumn ops behind it in the queue never run.
        expect(started).toEqual(['createTable']);
        expect(succeeded).toEqual([]);
        done();
      },
    });
  });

  it('a createTable succeeding lets its dependent addColumn run next, in order (scenario B end-to-end)', (done) => {
    const order: string[] = [];
    const run = (op: string): Observable<void> => {
      order.push(`start:${op}`);
      return of(undefined);
    };
    runQueuedOpsSequentially<string>(['createTable', 'addColumn'], run, (op) => order.push(`done:${op}`)).subscribe((ok) => {
      expect(ok).toBeTrue();
      expect(order).toEqual(['start:createTable', 'done:createTable', 'start:addColumn', 'done:addColumn']);
      done();
    });
  });
});

// ── Suggested / Pending / Confirmed (dbo.Patient investigation fix) ─────────────────────────────────────
function table(fullName: string, origin?: 'probed' | 'userCreated' | 'restoredUnverified'): DestinationTable {
  return { schemaName: 'dbo', tableName: fullName.split('.')[1], fullName, columns: [], origin };
}

describe('confirmedTableNames', () => {
  it('includes a live-probed table (origin undefined, same as every real probe() response) (scenario B)', () => {
    expect(confirmedTableNames([table('dbo.Patient', undefined)])).toEqual(['dbo.Patient']);
  });

  it('includes a table explicitly tagged "probed" (post-reopen live re-probe) (scenario B)', () => {
    expect(confirmedTableNames([table('dbo.Patient', 'probed')])).toEqual(['dbo.Patient']);
  });

  it('includes a "userCreated" table whether or not its create has flushed yet (scenario C/I — pendingTableNames is what further distinguishes staged-vs-flushed)', () => {
    expect(confirmedTableNames([table('dbo.Patient', 'userCreated')])).toEqual(['dbo.Patient']);
  });

  it('EXCLUDES a "restoredUnverified" table — the exact fix: a stale cached table must never look confirmed (scenario D)', () => {
    expect(confirmedTableNames([table('dbo.Patient', 'restoredUnverified')])).toEqual([]);
  });

  it('a table dropped from the live probe response is simply absent — "deleted from SQL Server" (scenario E)', () => {
    // Simulates _refreshSqlTablesFromLiveSchema's applyTables: a fresh live response that genuinely has no
    // dbo.Patient (it was dropped from the real database after the mapping was last saved) replaces the
    // whole list — there is no entry left to even be 'restoredUnverified' anymore.
    const liveResponse = [table('dbo.Other', 'probed')];
    expect(confirmedTableNames(liveResponse)).toEqual(['dbo.Other']);
  });

  it('is empty for a resource\'s pure default-guessed target that was never in sqlTables() at all (scenario A)', () => {
    // dbo.Patient seeded into targetByResource from DEST_RESOURCE_DEFS never even reaches sqlTables() —
    // there's nothing here for confirmedTableNames to exclude; it's just never present.
    expect(confirmedTableNames([])).toEqual([]);
  });
});

// ── findConfirmedTableMatch (MySQL/PostgreSQL existing-table auto-attach fix) ───────────────────────────
describe('findConfirmedTableMatch', () => {
  it('SQL Server: matches "dbo.Patient" (the qualified candidate) against fullName — unchanged, existing behavior', () => {
    const tables = [liveTable('dbo.Patient', 'Patient', 'probed')];
    expect(findConfirmedTableMatch('dbo.Patient', tables)).toEqual(tables[0]);
  });

  it('SQL Server: no match when no live table has this fullName', () => {
    const tables = [liveTable('dbo.Other', 'Other', 'probed')];
    expect(findConfirmedTableMatch('dbo.Patient', tables)).toBeNull();
  });

  it('MySQL: matches bare "Patient" against a live table whose fullName is "{database}.Patient" via its tableName', () => {
    const tables = [liveTable('fhirbridge_output.Patient', 'Patient', 'probed')];
    expect(findConfirmedTableMatch('Patient', tables)).toEqual(tables[0]);
  });

  it('MySQL: no match when the database genuinely has no "Patient" table', () => {
    const tables = [liveTable('fhirbridge_output.Other', 'Other', 'probed')];
    expect(findConfirmedTableMatch('Patient', tables)).toBeNull();
  });

  it('MySQL: also matches a "userCreated" table this session, whose fullName is itself bare (no schema to split on)', () => {
    const tables = [liveTable('Patient', 'Patient', 'userCreated')];
    expect(findConfirmedTableMatch('Patient', tables)).toEqual(tables[0]);
  });

  it('PostgreSQL: matches bare "Patient" against a live table whose fullName is "public.Patient" via its tableName', () => {
    const tables = [liveTable('public.Patient', 'Patient', 'probed')];
    expect(findConfirmedTableMatch('Patient', tables)).toEqual(tables[0]);
  });

  it('PostgreSQL: no match when the schema genuinely has no "Patient" table', () => {
    const tables = [liveTable('public.Other', 'Other', 'probed')];
    expect(findConfirmedTableMatch('Patient', tables)).toBeNull();
  });

  it('matches case-insensitively on fullName (SQL Server) and on tableName (MySQL/PostgreSQL) alike', () => {
    expect(findConfirmedTableMatch('DBO.PATIENT', [liveTable('dbo.Patient', 'Patient', 'probed')])).not.toBeNull();
    expect(findConfirmedTableMatch('patient', [liveTable('fhirbridge_output.PATIENT', 'PATIENT', 'probed')])).not.toBeNull();
    expect(findConfirmedTableMatch('PATIENT', [liveTable('public.patient', 'patient', 'probed')])).not.toBeNull();
  });

  it('does NOT match a "restoredUnverified" table by tableName either — no fake/suggested table may auto-attach; only the live schema or a table genuinely created this session counts', () => {
    const tables = [liveTable('fhirbridge_output.Patient', 'Patient', 'restoredUnverified')];
    expect(findConfirmedTableMatch('Patient', tables)).toBeNull();
  });

  it('returns null (not undefined) for an empty table list — never-probed / empty database', () => {
    expect(findConfirmedTableMatch('Patient', [])).toBeNull();
  });
});

describe('tableTargetStatus', () => {
  // 'suggested' (a default-guessed target shown before anything confirmed it existed, e.g.
  // DEST_RESOURCE_DEFS' "dbo.Patient") no longer exists as a state — see "Remove Suggested Tables from
  // Map Fields": a resource's target is never set to an unconfirmed guess in the first place any more
  // (DestinationWizardComponent._rebuildRows), and the card isn't even rendered unless the name is
  // genuinely pending or confirmed (FieldMappingCanvasComponent.isPrimaryTargetValid) — so this function
  // is now only ever asked to distinguish those two.

  it('is "pending" for a name with an unflushed queued op (scenario C)', () => {
    expect(tableTargetStatus('dbo.PatientNew', new Set(['dbo.PatientNew']))).toBe('pending');
  });

  it('is "confirmed" for a name with nothing queued against it (scenario B/H)', () => {
    expect(tableTargetStatus('dbo.Patient', new Set())).toBe('confirmed');
  });

  it('walks Pending -> Confirmed as a create-table op is queued then flushed (scenario I)', () => {
    // "Create a new table…" clicked: it's in pendingTableNames until the queue flushes.
    expect(tableTargetStatus('dbo.PatientAudit', new Set(['dbo.PatientAudit']))).toBe('pending');

    // "Add to Pipeline" flushes it for real: onSchemaOpQueued's op is removed from pendingSchemaOps, so
    // pendingTableNames no longer has it — now genuinely confirmed. Two more columns added after this point
    // (the rest of scenario I) all see the exact same 'confirmed' status.
    expect(tableTargetStatus('dbo.PatientAudit', new Set())).toBe('confirmed');
  });
});

describe('describeSchemaVerificationOutcome (scenario F — live probe failure)', () => {
  it('reports "failed" with a warning when there was no credentials to even attempt a probe with (result: null)', () => {
    const outcome = describeSchemaVerificationOutcome(null);
    expect(outcome.state).toBe('failed');
    expect(outcome.warning).toContain('Could not verify');
  });

  it('reports "failed" with the probe\'s own error text when it ran but could not connect', () => {
    const outcome = describeSchemaVerificationOutcome({ connected: false, error: 'Login failed for user.' });
    expect(outcome.state).toBe('failed');
    expect(outcome.warning).toBe('Login failed for user.');
  });

  it('falls back to a generic warning when a failed probe carries no error text', () => {
    const outcome = describeSchemaVerificationOutcome({ connected: false, error: null });
    expect(outcome.state).toBe('failed');
    expect(outcome.warning).toContain('Could not verify');
  });

  it('reports "verified" with no warning when the probe actually connected', () => {
    const outcome = describeSchemaVerificationOutcome({ connected: true, error: null });
    expect(outcome.state).toBe('verified');
    expect(outcome.warning).toBeNull();
  });
});
