import { TestBed } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';
import { EhrLiveWriteCheckService, UNKNOWN_LIVE_EHR, hasCompletedRunSince, latestOf, liveEhrWritesOf } from './ehr-live-write-check.service';
import { WorkflowApiService, WorkflowDefinitionDto, WorkflowNodeDto, WorkflowRunDto } from './workflow-api.service';
import { ISourceConnectionService } from '../source-connections/services/i-source-connection.service';
import { SourceConnectionModel } from '../source-connections/models/source-connection.model';

const node = (fields: Record<string, string>): WorkflowNodeDto =>
  ({ id: 'n', configurationJson: JSON.stringify({ __transformId: 'dest-ehr-writeback', ...fields }) }) as WorkflowNodeDto;
/** Last saved on 2026-10-02 unless a test says otherwise. */
const definition = (...nodes: WorkflowNodeDto[]) =>
  ({ nodes, createdOnUtc: '2026-09-01T00:00:00Z', updatedOnUtc: '2026-10-02T00:00:00Z' }) as WorkflowDefinitionDto;
const succeededAt = (startedAt: string) => [{ id: '1', status: 'Succeeded', startedAt }] as WorkflowRunDto[];
const connection = (id: string, modifiedOnUtc: string | null, createdOnUtc = '2026-01-01T00:00:00Z') =>
  ({ id, modifiedOnUtc, createdOnUtc }) as SourceConnectionModel;

/** Before a run: a live EHR write-back that has not completed a run asks "Write into <EHR> now?". */
describe('EhrLiveWriteCheckService', () => {
  it('finds the live EHR write-backs, never a test run or a dry run; a plain FHIR server written for real counts', () => {
    expect(liveEhrWritesOf(definition(
      node({ dest_ehrVendor: 'Epic', dest_dryRun: 'false' }),
      node({ dest_ehrVendor: 'Healow', dest_dryRun: 'false', dest_testAsVendor: 'Healow' }),
      node({ dest_ehrVendor: 'Athenahealth', dest_dryRun: 'true' }),
      node({ dest_ehrVendor: 'GenericFhir', dest_dryRun: 'false' }),
      { id: 'x', configurationJson: '{bad' } as WorkflowNodeDto,
    ))).toEqual(['Epic', 'FHIR test server']);
  });

  it('counts only a successful run since the workflow was last saved', () => {
    const runs = succeededAt('2026-10-01T00:00:00Z');
    expect(hasCompletedRunSince(runs, '2026-09-30T00:00:00Z')).toBeTrue();
    expect(hasCompletedRunSince(runs, '2026-10-02T00:00:00Z')).toBeFalse();
    expect(hasCompletedRunSince([{ id: '2', status: 'Failed', startedAt: '2026-10-03T00:00:00Z' }], null)).toBeFalse();
  });

  it('takes the latest readable time', () => {
    expect(latestOf(['2026-10-02T00:00:00Z', null, 'bad', '2026-10-05T00:00:00Z', undefined])).toBe('2026-10-05T00:00:00Z');
    expect(latestOf([null, undefined])).toBeNull();
  });

  interface Fakes {
    load?: () => Observable<WorkflowDefinitionDto>;
    runs?: () => Observable<WorkflowRunDto[]>;
    connections?: () => Observable<SourceConnectionModel[]>;
    known?: string[] | null;
  }

  function check(def: WorkflowDefinitionDto, fakes: Fakes = {}): string | null | undefined {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        { provide: WorkflowApiService, useValue: { load: fakes.load ?? (() => of(def)), runs: fakes.runs ?? (() => of([])) } },
        { provide: ISourceConnectionService, useValue: { getAll: fakes.connections ?? (() => of([])) } },
      ],
    });
    let result: string | null | undefined;
    TestBed.inject(EhrLiveWriteCheckService).firstLiveWrite('w1', fakes.known).subscribe(r => (result = r));
    return result;
  }

  it('asks about the EHR when the workflow has not run since it was saved', () => {
    expect(check(definition(node({ dest_ehrVendor: 'Healow', dest_dryRun: 'false' })))).toBe('eClinicalWorks');
  });

  it('does not ask once a run has completed, or when nothing is live', () => {
    expect(check(definition(node({ dest_ehrVendor: 'Epic', dest_dryRun: 'false' })), { runs: () => of(succeededAt('2026-10-03T00:00:00Z')) })).toBeNull();
    const runs = jasmine.createSpy('runs');
    expect(check(definition(node({ dest_ehrVendor: 'Epic', dest_dryRun: 'true' })), { runs })).toBeNull();
    expect(runs).not.toHaveBeenCalled();
  });

  it('names every live EHR', () => {
    expect(check(definition(
      node({ dest_ehrVendor: 'Epic', dest_dryRun: 'false' }),
      node({ dest_ehrVendor: 'Athenahealth', dest_dryRun: 'false' }),
    ))).toBe('Epic and athenahealth');
  });

  it('asks when the run history cannot be read', () => {
    expect(check(definition(node({ dest_ehrVendor: 'Epic', dest_dryRun: 'false' })), { runs: () => throwError(() => new Error('x')) })).toBe('Epic');
  });

  // A workflow that cannot be read is asked about, never run without asking.
  it('asks when the workflow cannot be read, naming the EHRs the list row knows', () => {
    const load = () => throwError(() => new Error('timeout'));
    expect(check(definition(), { load, known: ['Epic', 'Healow'] })).toBe('Epic and eClinicalWorks');
    expect(check(definition(), { load, known: [] })).toBe(UNKNOWN_LIVE_EHR);
    expect(check(definition(), { load })).toBe(UNKNOWN_LIVE_EHR);
  });

  // A node migrated to the {"ref", "config"} envelope is still a live write.
  it('reads a live write-back saved under "config"', () => {
    const enveloped = {
      id: 'n',
      configurationJson: JSON.stringify({ ref: { id: 'r' }, config: { __transformId: 'dest-ehr-writeback', dest_ehrVendor: 'Epic', dest_dryRun: 'false' } }),
    } as WorkflowNodeDto;
    expect(check(definition(enveloped))).toBe('Epic');
  });

  // The modified time comes from the definition loaded now, not from the caller's (possibly stale) list row.
  it("compares runs with the freshly loaded definition's last save", () => {
    const live = node({ dest_ehrVendor: 'Epic', dest_dryRun: 'false' });
    const runs = () => of(succeededAt('2026-10-01T09:30:00Z'));
    // Switched to live at 10:05, after the 09:30 run: asks.
    expect(check({ ...definition(live), updatedOnUtc: '2026-10-01T10:05:00Z' }, { runs })).toBe('Epic');
    // Saved once only (no update yet): the creation time counts.
    expect(check({ ...definition(live), createdOnUtc: '2026-10-01T10:05:00Z', updatedOnUtc: null }, { runs })).toBe('Epic');
    expect(check({ ...definition(live), createdOnUtc: '2026-10-01T09:00:00Z', updatedOnUtc: null }, { runs })).toBeNull();
  });

  // A change to the write connection in Settings (activation, base URL) asks again.
  describe('the connection the write goes through', () => {
    const live = node({ dest_ehrVendor: 'Healow', dest_dryRun: 'false', dest_sourceConnectionId: 'C-1' });
    const runs = () => of(succeededAt('2026-10-03T00:00:00Z'));

    it('asks again once the connection was changed after the last successful run', () => {
      expect(check(definition(live), { runs, connections: () => of([connection('c-1', '2026-10-04T00:00:00Z')]) })).toBe('eClinicalWorks');
    });

    it('does not ask when the connection has not changed since that run; other connections do not count', () => {
      expect(check(definition(live), {
        runs,
        connections: () => of([connection('c-1', '2026-09-01T00:00:00Z'), connection('c-2', '2026-10-09T00:00:00Z')]),
      })).toBeNull();
      expect(check(definition(live), { runs, connections: () => of([connection('c-1', null, '2026-09-01T00:00:00Z')]) })).toBeNull();
    });

    it('asks when the connections cannot be read', () => {
      expect(check(definition(live), { runs, connections: () => throwError(() => new Error('403')) })).toBe('eClinicalWorks');
    });

    it('reads no connections when no live write names one', () => {
      const connections = jasmine.createSpy('getAll');
      expect(check(definition(node({ dest_ehrVendor: 'Epic', dest_dryRun: 'false' })), { runs, connections })).toBeNull();
      expect(connections).not.toHaveBeenCalled();
    });
  });
});
