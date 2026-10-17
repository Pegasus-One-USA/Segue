import {
  WritableTarget,
  connectionGroupsFor,
  ehrReviewLines,
  isEhrWriteVendor,
  isTestableVendor,
  runModeOf,
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

/** Run mode is saved as dest_dryRun + dest_testAsVendor; runModeOf reads it back as a reopened node and Review do. */
describe('ehr-write-back.model', () => {
  describe('runModeOf', () => {
    it('reads a live run, a dry run and a test run', () => {
      expect(runModeOf({ dest_dryRun: 'false' })).toBe('live');
      expect(runModeOf({ dest_dryRun: 'true' })).toBe('dryRun');
      expect(runModeOf({ dest_dryRun: 'false', dest_testAsVendor: 'Epic' })).toBe('test');
    });

    it('treats anything but an explicit false as a dry run, as the executor does', () => {
      expect(runModeOf({})).toBe('dryRun');
      expect(runModeOf({ dest_dryRun: '' })).toBe('dryRun');
      expect(runModeOf({ dest_dryRun: 'FALSE' })).toBe('dryRun');
    });

    it('a dry run over a test server is a dry run', () => {
      expect(runModeOf({ dest_dryRun: 'true', dest_testAsVendor: 'Healow' })).toBe('dryRun');
    });

    it('ignores a test-as vendor that cannot be tested', () => {
      expect(runModeOf({ dest_dryRun: 'false', dest_testAsVendor: 'GenericFhir' })).toBe('live');
      expect(runModeOf({ dest_dryRun: 'false', dest_testAsVendor: 'Cerner' })).toBe('live');
    });
  });

  describe('connectionGroupsFor', () => {
    const targets = [
      target('epic', 'Epic'),
      target('athena', 'Athenahealth'),
      target('hapi-all', 'GenericFhir', ['Epic', 'Healow', 'Athenahealth']),
      target('hapi-ecw', 'GenericFhir', ['Healow']),
    ];
    const ids = (list: WritableTarget[]) => list.map(t => t.id);

    it("groups the EHR's own connections and the test servers that stand in for it", () => {
      const epic = connectionGroupsFor(targets, 'Epic');
      expect(ids(epic.own)).toEqual(['epic']);
      expect(ids(epic.testServers)).toEqual(['hapi-all']);
      expect(ids(connectionGroupsFor(targets, 'Healow').testServers)).toEqual(['hapi-all', 'hapi-ecw']);
    });

    it('a FHIR server has no test servers of its own', () => {
      const fhir = connectionGroupsFor(targets, 'GenericFhir');
      expect(ids(fhir.own)).toEqual(['hapi-all', 'hapi-ecw']);
      expect(fhir.testServers).toEqual([]);
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
    expect(vendorLabel('GenericFhir')).toBe('FHIR test server');
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
        mode: 'Test run: nothing reaches eClinicalWorks, up to 200 records per run',
      });
    });

    it('a dry run over a test server', () => {
      expect(ehrReviewLines(config({ dest_dryRun: 'true', dest_testAsVendor: 'Healow' }), 'Healow', 'HAPI')).toEqual({
        writesTo: 'HAPI (FHIR test server), shaped as eClinicalWorks',
        mode: 'Dry run: checks every record, sends nothing, up to 200 records per run',
      });
    });

    it('falls back to the saved vendor and a plain connection name', () => {
      expect(ehrReviewLines(config({ dest_dryRun: 'true' }), null, null).writesTo)
        .toBe('the chosen connection (eClinicalWorks)');
    });
  });
});
