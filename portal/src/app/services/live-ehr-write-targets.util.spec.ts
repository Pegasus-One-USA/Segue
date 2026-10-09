import {
  addedLiveEhrWriteTargets,
  ehrNamesOf,
  liveEhrWriteTargetsOfCanvas,
  liveEhrWriteTargetsOfDefinition,
} from './live-ehr-write-targets.util';
import { CanvasNode } from '../models/node-v2.model';
import { WorkflowNodeDto } from './workflow-api.service';

const writeBack = (id: string, fields: Record<string, string>): CanvasNode =>
  ({ id, kind: 'transform', transformId: 'dest-ehr-writeback', x: 0, y: 0, fields }) as CanvasNode;
const live = (vendor: string, connection = 'c-1') => ({ dest_ehrVendor: vendor, dest_dryRun: 'false', dest_sourceConnectionId: connection });

/** The live EHR writes a workflow has, and which of them a save would add. */
describe('live EHR write targets', () => {
  it('counts a live write-back, never a test run or a dry run; a plain FHIR server written for real counts', () => {
    const targets = liveEhrWriteTargetsOfCanvas([
      writeBack('n1', live('Epic')),
      writeBack('n2', { ...live('GenericFhir'), dest_testAsVendor: 'Healow' }),
      writeBack('n3', { dest_ehrVendor: 'Athenahealth', dest_dryRun: 'true' }),
      writeBack('n4', { dest_ehrVendor: 'Athenahealth' }),
      writeBack('n5', live('GenericFhir', 'c-2')),
      { id: 'n6', kind: 'transform', transformId: 'dest-csv', x: 0, y: 0, fields: live('Epic') } as CanvasNode,
    ]);
    expect(targets).toEqual([
      { nodeId: 'n1', vendor: 'Epic', connectionId: 'c-1' },
      { nodeId: 'n5', vendor: 'GenericFhir', connectionId: 'c-2' },
    ]);
  });

  it('reads a saved definition the same way, skipping a node it cannot read', () => {
    const nodes = [
      { id: 'n1', configurationJson: JSON.stringify({ __transformId: 'dest-ehr-writeback', ...live('Healow') }) },
      { id: 'n2', configurationJson: '{bad' },
      { id: 'n3', configurationJson: JSON.stringify({ __transformId: 'dest-ehr-writeback', ...live('Epic') }), isEnabled: false },
    ] as WorkflowNodeDto[];
    expect(liveEhrWriteTargetsOfDefinition(nodes)).toEqual([{ nodeId: 'n1', vendor: 'Healow', connectionId: 'c-1' }]);
  });

  it('a save adds a new live node, a test or dry run switched to live, and a live node on another connection', () => {
    const saved = liveEhrWriteTargetsOfCanvas([writeBack('n1', live('Epic'))]);
    expect(addedLiveEhrWriteTargets(saved, saved)).toEqual([]);
    expect(addedLiveEhrWriteTargets([], saved).length).toBe(1);
    expect(addedLiveEhrWriteTargets(saved, liveEhrWriteTargetsOfCanvas([writeBack('n1', live('Epic', 'c-9'))])).length).toBe(1);
    expect(addedLiveEhrWriteTargets(saved, liveEhrWriteTargetsOfCanvas([writeBack('n1', live('Epic')), writeBack('n2', live('Healow'))])))
      .toEqual([{ nodeId: 'n2', vendor: 'Healow', connectionId: 'c-1' }]);
    expect(addedLiveEhrWriteTargets(saved, [])).toEqual([]);
  });

  it("names every EHR by its own name, once", () => {
    expect(ehrNamesOf(['Epic'])).toBe('Epic');
    expect(ehrNamesOf(['Healow'])).toBe('eClinicalWorks');
    expect(ehrNamesOf(['Epic', 'Athenahealth', 'Epic'])).toBe('Epic and athenahealth');
    expect(ehrNamesOf(['Epic', 'Healow', 'Athenahealth'])).toBe('Epic, eClinicalWorks and athenahealth');
    expect(ehrNamesOf(['GenericFhir'])).toBe('FHIR test server');
    expect(ehrNamesOf([])).toBe('');
  });
});
