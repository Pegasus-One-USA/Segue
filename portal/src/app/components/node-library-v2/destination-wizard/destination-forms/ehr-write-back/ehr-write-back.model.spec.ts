import {
  WritableTarget,
  connectionsFor,
  ehrReviewLines,
  isEhrWriteVendor,
  isTestableVendor,
  runModeFields,
  runModeOf,
  runModesFor,
  savedWriteVendorOf,
  vendorLabel,
} from './ehr-write-back.model';

function target(id: string, vendor: string, testableVendors: string[] = []): WritableTarget {
  return {
    id, name: id, vendor, resourceTypes: ['AllergyIntolerance'], liveTypes: [], awaitingActivationTypes: [],
    offersHolderEncounter: false, cloneModeEnabled: false, testableVendors, optInApis: [], departmentId: null,
    capabilities: [],
  };
}

/** Run mode is saved as dest_dryRun + dest_testAsVendor; these helpers are the only translation both ways. */
describe('ehr-write-back.model', () => {
  describe('runModeOf', () => {
    it('reads a live run, a dry run and a test run', () => {
      expect(runModeOf({ dest_dryRun: 'false' })).toEqual({ mode: 'live', droppedTestServer: false });
      expect(runModeOf({ dest_dryRun: 'true' })).toEqual({ mode: 'dryRun', droppedTestServer: false });
      expect(runModeOf({ dest_dryRun: 'false', dest_testAsVendor: 'Epic' })).toEqual({ mode: 'test', droppedTestServer: false });
    });

    it('treats anything but an explicit false as a dry run, as the executor does', () => {
      expect(runModeOf({}).mode).toBe('dryRun');
      expect(runModeOf({ dest_dryRun: '' }).mode).toBe('dryRun');
      expect(runModeOf({ dest_dryRun: 'FALSE' }).mode).toBe('dryRun');
    });

    it('reopens a test run that was also a dry run as a plain dry run, flagging the dropped test server', () => {
      expect(runModeOf({ dest_dryRun: 'true', dest_testAsVendor: 'Healow' })).toEqual({ mode: 'dryRun', droppedTestServer: true });
      expect(runModeOf({ dest_testAsVendor: 'Epic' })).toEqual({ mode: 'dryRun', droppedTestServer: true });
    });

    it('ignores a test-as vendor that cannot be tested', () => {
      expect(runModeOf({ dest_dryRun: 'false', dest_testAsVendor: 'GenericFhir' })).toEqual({ mode: 'live', droppedTestServer: false });
      expect(runModeOf({ dest_dryRun: 'false', dest_testAsVendor: 'Cerner' })).toEqual({ mode: 'live', droppedTestServer: false });
    });
  });

  describe('runModeFields', () => {
    it('writes each mode', () => {
      expect(runModeFields('live', 'Epic')).toEqual({ dest_dryRun: 'false', dest_testAsVendor: '' });
      expect(runModeFields('dryRun', 'Athenahealth')).toEqual({ dest_dryRun: 'true', dest_testAsVendor: '' });
      expect(runModeFields('test', 'Healow')).toEqual({ dest_dryRun: 'false', dest_testAsVendor: 'Healow' });
      expect(runModeFields('live', 'GenericFhir')).toEqual({ dest_dryRun: 'false', dest_testAsVendor: '' });
      expect(runModeFields('dryRun', null)).toEqual({ dest_dryRun: 'true', dest_testAsVendor: '' });
    });

    it('refuses a test run for a vendor a test server cannot stand in for', () => {
      expect(() => runModeFields('test', 'GenericFhir')).toThrow();
      expect(() => runModeFields('test', null)).toThrow();
    });

    it('round-trips through runModeOf', () => {
      for (const mode of ['live', 'dryRun', 'test'] as const) {
        expect(runModeOf(runModeFields(mode, 'Epic')).mode).toBe(mode);
      }
    });
  });

  it('offers Test only for a vendor a test server can stand in for', () => {
    expect(runModesFor('Epic')).toEqual(['live', 'dryRun', 'test']);
    expect(runModesFor('Healow')).toEqual(['live', 'dryRun', 'test']);
    expect(runModesFor('Athenahealth')).toEqual(['live', 'dryRun', 'test']);
    expect(runModesFor('GenericFhir')).toEqual(['live', 'dryRun']);
    expect(runModesFor(null)).toEqual(['live', 'dryRun']);
  });

  describe('connectionsFor', () => {
    const targets = [
      target('epic', 'Epic'),
      target('athena', 'Athenahealth'),
      target('hapi-all', 'GenericFhir', ['Epic', 'Healow', 'Athenahealth']),
      target('hapi-ecw', 'GenericFhir', ['Healow']),
    ];
    const ids = (list: WritableTarget[]) => list.map(t => t.id);

    it('offers the vendor\'s own connections for Live and Dry run', () => {
      expect(ids(connectionsFor(targets, 'Epic', 'live'))).toEqual(['epic']);
      expect(ids(connectionsFor(targets, 'Athenahealth', 'dryRun'))).toEqual(['athena']);
      expect(ids(connectionsFor(targets, 'GenericFhir', 'live'))).toEqual(['hapi-all', 'hapi-ecw']);
    });

    it('offers FHIR servers that can stand in for the vendor in a test run', () => {
      expect(ids(connectionsFor(targets, 'Epic', 'test'))).toEqual(['hapi-all']);
      expect(ids(connectionsFor(targets, 'Healow', 'test'))).toEqual(['hapi-all', 'hapi-ecw']);
    });

    it('offers everything while no vendor is known', () => {
      expect(ids(connectionsFor(targets, null, 'live'))).toEqual(ids(targets));
      expect(ids(connectionsFor(targets, null, 'dryRun'))).toEqual(ids(targets));
    });
  });

  it('knows the write vendors and which can be tested', () => {
    expect(isTestableVendor('Epic')).toBeTrue();
    expect(isTestableVendor('Healow')).toBeTrue();
    expect(isTestableVendor('Athenahealth')).toBeTrue();
    expect(isTestableVendor('GenericFhir')).toBeFalse();
    expect(isTestableVendor('')).toBeFalse();
    expect(isTestableVendor(undefined)).toBeFalse();
    expect(isEhrWriteVendor('GenericFhir')).toBeTrue();
    expect(isEhrWriteVendor('Cerner')).toBeFalse();
  });

  it('labels vendors as admins call them', () => {
    expect(vendorLabel('Healow')).toBe('eClinicalWorks');
    expect(vendorLabel('GenericFhir')).toBe('FHIR server');
    expect(vendorLabel('Other')).toBe('Other');
    expect(vendorLabel(null)).toBe('');
  });

  describe('savedWriteVendorOf', () => {
    it('reads the vendor a test run stands in for first, so a legacy test run saved as GenericFhir is its EHR', () => {
      expect(savedWriteVendorOf({ dest_ehrVendor: 'GenericFhir', dest_testAsVendor: 'Epic', dest_dryRun: 'false' })).toBe('Epic');
      expect(savedWriteVendorOf({ dest_ehrVendor: 'GenericFhir', dest_testAsVendor: 'Epic', dest_dryRun: 'true' })).toBe('Epic');
    });

    it('otherwise the vendor written to', () => {
      expect(savedWriteVendorOf({ dest_ehrVendor: 'Healow' })).toBe('Healow');
      expect(savedWriteVendorOf({ dest_ehrVendor: 'GenericFhir', dest_testAsVendor: '' })).toBe('GenericFhir');
    });

    it('ignores a test-as vendor that cannot be tested, and names nothing it does not know', () => {
      expect(savedWriteVendorOf({ dest_ehrVendor: 'Athenahealth', dest_testAsVendor: 'GenericFhir' })).toBe('Athenahealth');
      expect(savedWriteVendorOf({ dest_ehrVendor: 'Cerner' })).toBeNull();
      expect(savedWriteVendorOf({})).toBeNull();
    });
  });

  describe('ehrReviewLines', () => {
    const config = (fields: Record<string, string>) => ({ dest_ehrVendor: 'Healow', dest_maxWritesPerRun: '200', ...fields });

    it('a live run', () => {
      expect(ehrReviewLines(config({ dest_dryRun: 'false' }), 'Healow', 'Epic prod')).toEqual({
        writesTo: 'Epic prod (eClinicalWorks)',
        mode: 'Live: writes into eClinicalWorks, up to 200 records per run',
      });
    });

    it('a dry run', () => {
      expect(ehrReviewLines(config({ dest_dryRun: 'true' }), 'Healow', 'Epic prod').mode)
        .toBe('Dry run: checks every record, sends nothing, up to 200 records per run');
    });

    it('a test run', () => {
      expect(ehrReviewLines(config({ dest_dryRun: 'false', dest_testAsVendor: 'Healow' }), 'Healow', 'Epic prod')).toEqual({
        writesTo: 'Epic prod (FHIR test server), shaped as eClinicalWorks',
        mode: 'Test on FHIR server: nothing reaches eClinicalWorks, up to 200 records per run',
      });
    });

    it('falls back to the saved vendor and a plain connection name', () => {
      expect(ehrReviewLines(config({ dest_dryRun: 'true' }), null, null).writesTo)
        .toBe('the chosen connection (eClinicalWorks)');
    });
  });
});
