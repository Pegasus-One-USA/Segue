import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { EhrLiveWriteCheckService, hasCompletedRunSince, liveEhrWritesOf } from './ehr-live-write-check.service';
import { WorkflowApiService, WorkflowDefinitionDto, WorkflowNodeDto, WorkflowRunDto } from './workflow-api.service';

const node = (fields: Record<string, string>): WorkflowNodeDto =>
  ({ id: 'n', configurationJson: JSON.stringify({ __transformId: 'dest-ehr-writeback', ...fields }) }) as WorkflowNodeDto;
const definition = (...nodes: WorkflowNodeDto[]) => ({ nodes }) as WorkflowDefinitionDto;

/** Before a run: a live EHR write-back that has not completed a run asks "Write into <EHR> now?". */
describe('EhrLiveWriteCheckService', () => {
  it('finds the live EHR write-backs, never a test run, a dry run or a plain FHIR server', () => {
    expect(liveEhrWritesOf(definition(
      node({ dest_ehrVendor: 'Epic', dest_dryRun: 'false' }),
      node({ dest_ehrVendor: 'Healow', dest_dryRun: 'false', dest_testAsVendor: 'Healow' }),
      node({ dest_ehrVendor: 'Athenahealth', dest_dryRun: 'true' }),
      node({ dest_ehrVendor: 'GenericFhir', dest_dryRun: 'false' }),
      { id: 'x', configurationJson: '{bad' } as WorkflowNodeDto,
    ))).toEqual(['Epic']);
  });

  it('counts only a successful run since the workflow was last saved', () => {
    const runs = [{ id: '1', status: 'Succeeded', startedAt: '2026-10-01T00:00:00Z' }] as WorkflowRunDto[];
    expect(hasCompletedRunSince(runs, '2026-09-30T00:00:00Z')).toBeTrue();
    expect(hasCompletedRunSince(runs, '2026-10-02T00:00:00Z')).toBeFalse();
    expect(hasCompletedRunSince([{ id: '2', status: 'Failed', startedAt: '2026-10-03T00:00:00Z' }], null)).toBeFalse();
  });

  function check(def: WorkflowDefinitionDto, runs: () => ReturnType<WorkflowApiService['runs']>): string | null | undefined {
    TestBed.configureTestingModule({
      providers: [{ provide: WorkflowApiService, useValue: { load: () => of(def), runs } }],
    });
    let result: string | null | undefined;
    TestBed.inject(EhrLiveWriteCheckService).firstLiveWrite('w1', '2026-10-02T00:00:00Z').subscribe(r => (result = r));
    return result;
  }

  it('asks about the EHR when the workflow has not run since it was saved', () => {
    expect(check(definition(node({ dest_ehrVendor: 'Healow', dest_dryRun: 'false' })), () => of([]))).toBe('eClinicalWorks');
  });

  it('does not ask once a run has completed, or when nothing is live', () => {
    const done = [{ id: '1', status: 'Succeeded', startedAt: '2026-10-03T00:00:00Z' }] as WorkflowRunDto[];
    expect(check(definition(node({ dest_ehrVendor: 'Epic', dest_dryRun: 'false' })), () => of(done))).toBeNull();
    TestBed.resetTestingModule();
    const runs = jasmine.createSpy('runs');
    expect(check(definition(node({ dest_ehrVendor: 'Epic', dest_dryRun: 'true' })), runs)).toBeNull();
    expect(runs).not.toHaveBeenCalled();
  });

  it('asks when the run history cannot be read', () => {
    expect(check(definition(node({ dest_ehrVendor: 'Epic', dest_dryRun: 'false' })), () => throwError(() => new Error('x')))).toBe('Epic');
  });
});
