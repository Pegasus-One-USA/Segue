import { Observable, of, throwError } from 'rxjs';
import { AddColumnRequest, CreateTableRequest, DestinationProbeRequest } from '../../../../services/destination-schema.service';
import { PendingSchemaOp } from './field-mapping-model';
import {
  canQueueAddColumn, computePendingTableNames, describeCreateTableConflict, describeLiveCreateTableConflict,
  isConfirmedRealTable, runQueuedOpsSequentially,
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
  it('is true for a table from the live probe with nothing pending against it (scenario A)', () => {
    expect(isConfirmedRealTable('dbo.Patient', ['dbo.Patient'], new Set())).toBeTrue();
  });

  it('is false for a table that is only known because of a still-pending op (scenario B)', () => {
    // sqlTableOptions already lists it (optimistic preview applied the moment the create was queued —
    // see onTableCreated), but its createTable op hasn't flushed yet.
    const pending = new Set(['dbo.Patient']);
    expect(isConfirmedRealTable('dbo.Patient', ['dbo.Patient'], pending)).toBeFalse();
  });

  it('is false for a name absent from sqlTableOptions entirely (scenario D — DB table was deleted)', () => {
    expect(isConfirmedRealTable('dbo.Patient', [], new Set())).toBeFalse();
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
      extraTables: [], sqlTableOptions: [], pendingTableNames: new Set(),
    })).toBeNull();
  });

  it('blocks re-creating a table that already has a pending create staged, with a distinct message (the core fix)', () => {
    const msg = describeCreateTableConflict('dbo.Patient', {
      extraTables: [], sqlTableOptions: ['dbo.Patient'], pendingTableNames: new Set(['dbo.Patient']),
    });
    expect(msg).toContain('already staged in this session');
  });

  it('blocks creating a table confirmed to already exist for real, with the required exact wording', () => {
    const msg = describeCreateTableConflict('dbo.Patient', {
      extraTables: [], sqlTableOptions: ['dbo.Patient'], pendingTableNames: new Set(),
    });
    expect(msg).toBe("Table 'dbo.Patient' already exists. Please choose a different table name.");
  });

  it('blocks re-adding a real table already on the canvas as an extra table', () => {
    const msg = describeCreateTableConflict('dbo.PatientContact', {
      extraTables: ['dbo.PatientContact'], sqlTableOptions: [], pendingTableNames: new Set(),
    });
    expect(msg).toBe("Table 'dbo.PatientContact' already exists. Please choose a different table name.");
  });

  it('does NOT block a resource\'s own unfulfilled guessed target — this is the exact bug being fixed', () => {
    // Before the fix: a resource's default-guessed target (e.g. "dbo.Patient", never actually created)
    // made isPrimaryTargetValid/targetAlreadyReal true purely because the name matched, contradicting
    // AddColumnAsync's own live "the table does not exist" check. Neither extraTables nor sqlTableOptions
    // knows about this name at all here, so there is genuinely nothing to conflict with.
    const msg = describeCreateTableConflict('dbo.Patient', {
      extraTables: [], sqlTableOptions: [], pendingTableNames: new Set(),
    });
    expect(msg).toBeNull();
  });
});

describe('describeLiveCreateTableConflict', () => {
  it('blocks a table the live probe finds — the exact bug reported: a real DB table the local cache never learned about (scenario 1 / D)', () => {
    const msg = describeLiveCreateTableConflict('dbo.Patient', {
      connected: true, error: null, tables: [{ fullName: 'dbo.Patient' }, { fullName: 'dbo.Other' }],
    });
    expect(msg).toBe("Table 'dbo.Patient' already exists. Please choose a different table name.");
  });

  it('allows a table the live probe genuinely does not find (scenario 2)', () => {
    const msg = describeLiveCreateTableConflict('dbo.TestPatient', {
      connected: true, error: null, tables: [{ fullName: 'dbo.Patient' }],
    });
    expect(msg).toBeNull();
  });

  it('matches case-insensitively, mirroring SQL Server\'s default collation (scenario 6)', () => {
    // SqlDestinationSchemaService.TableExistsAsync's OBJECT_ID lookup treats "dbo.Patient" and
    // "dbo.patient" as the same real object — this check must agree, or a differently-cased duplicate
    // would slip past validation here only to fail for real once "Add to Pipeline" flushes it.
    const probe = { connected: true, error: null, tables: [{ fullName: 'dbo.Patient' }] };
    expect(describeLiveCreateTableConflict('dbo.Patient', probe)).not.toBeNull();
    expect(describeLiveCreateTableConflict('dbo.patient', probe)).not.toBeNull();
    expect(describeLiveCreateTableConflict('DBO.PATIENT', probe)).not.toBeNull();
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
