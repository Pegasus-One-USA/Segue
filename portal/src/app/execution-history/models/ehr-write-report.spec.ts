import {
  ehrWriteReasonLabel,
  ehrWriteReasons,
  ehrWriteReportToText,
  ehrWriteScopeLabel,
  ehrWriteTotals,
  parseEhrWriteReport,
} from './ehr-write-report';

// The delivery detail stored for a real Epic dry run in clone mode (counts and reason codes only).
const DRY_RUN_DETAIL = JSON.stringify({
  DestinationId: '5b22568d983942749f05ed51375df0e4',
  RecordsWritten: 0,
  WrittenAt: '2026-10-02T12:50:00+00:00',
  HasDownload: false,
  EhrWrite: {
    DryRun: true,
    CloneMode: true,
    TargetVendor: 'Epic',
    RecordsReceived: 37,
    ScopeStatus: 'verified',
    Resources: [
      { ResourceType: 'AllergyIntolerance', Received: 1, WouldWrite: 0, Written: 0, AlreadyWritten: 0, Skipped: 1, Rejected: 0, Unknown: 0,
        Reasons: { 'patient-not-yet-created': 1 } },
      { ResourceType: 'Condition', Received: 35, WouldWrite: 0, Written: 0, AlreadyWritten: 0, Skipped: 35, Rejected: 0, Unknown: 0,
        Reasons: { 'patient-not-yet-created': 4, 'not-a-problem-list-item': 31 } },
      { ResourceType: 'Patient', Received: 1, WouldWrite: 1, Written: 0, AlreadyWritten: 0, Skipped: 0, Rejected: 0, Unknown: 0,
        Reasons: {} },
    ],
  },
});

describe('parseEhrWriteReport', () => {
  it('reads the EHR write-back report from a destination delivery detail', () => {
    const report = parseEhrWriteReport(DRY_RUN_DETAIL);

    expect(report).not.toBeNull();
    expect(report!.DryRun).toBeTrue();
    expect(report!.CloneMode).toBeTrue();
    expect(report!.TargetVendor).toBe('Epic');
    expect(report!.RecordsReceived).toBe(37);
    expect(report!.Resources.map(r => r.ResourceType)).toEqual(['AllergyIntolerance', 'Condition', 'Patient']);
  });

  it('returns null for a destination that is not an EHR write-back', () => {
    expect(parseEhrWriteReport(JSON.stringify({ DestinationId: 'x', RecordsWritten: 12, HasDownload: false }))).toBeNull();
  });

  it('returns null for missing or malformed detail instead of throwing', () => {
    expect(parseEhrWriteReport(null)).toBeNull();
    expect(parseEhrWriteReport('')).toBeNull();
    expect(parseEhrWriteReport('{not json')).toBeNull();
    expect(parseEhrWriteReport(JSON.stringify({ EhrWrite: { Resources: 'nope' } }))).toBeNull();
  });

  it('treats missing or invalid counts as zero and drops malformed resource rows', () => {
    const report = parseEhrWriteReport(JSON.stringify({
      EhrWrite: {
        Resources: [
          { ResourceType: 'Condition', Received: '5', WouldWrite: -2, Reasons: { a: 2, b: 'x', c: 0 } },
          { Received: 3 },
          null,
        ],
      },
    }));

    expect(report!.ScopeStatus).toBe('unknown');
    expect(report!.Resources.length).toBe(1);
    expect(report!.Resources[0].Received).toBe(0);
    expect(report!.Resources[0].WouldWrite).toBe(0);
    expect(report!.Resources[0].Reasons).toEqual({ a: 2 });
  });
});

describe('ehrWriteTotals', () => {
  it('sums each outcome over every resource type', () => {
    const totals = ehrWriteTotals(parseEhrWriteReport(DRY_RUN_DETAIL)!);

    expect(totals).toEqual({ WouldWrite: 1, Written: 0, AlreadyWritten: 0, Skipped: 36, Rejected: 0, Unknown: 0 });
  });
});

describe('ehrWriteReasons', () => {
  it('lists reasons most frequent first, in plain words', () => {
    const condition = parseEhrWriteReport(DRY_RUN_DETAIL)!.Resources[1];

    expect(ehrWriteReasons(condition)).toEqual([
      { code: 'not-a-problem-list-item', label: 'Not a problem-list entry (the EHR accepts problems only)', count: 31 },
      { code: 'patient-not-yet-created', label: 'Waiting for the patient to be created', count: 4 },
    ]);
  });
});

describe('ehrWriteReasonLabel', () => {
  it('names dry-run-only types, and still reads the retired release reason on older runs', () => {
    expect(ehrWriteReasonLabel('live-write-not-supported')).toBe('This EHR accepts dry runs only for this type');
    expect(ehrWriteReasonLabel('live-write-not-released')).toBe('Type was not released for live writes (older run)');
    expect(ehrWriteReasonLabel('patient-not-selected')).toBe('Patient not selected under Resource types, so the patient cannot be created');
  });

  it('names why a linked note could not be read from the source', () => {
    expect(ehrWriteReasonLabel('note-content-fetch-failed')).toBe('Note content could not be read from the source (retried next run)');
    expect(ehrWriteReasonLabel('note-content-url-not-on-source')).toBe('Note content links outside the source system, so it was not fetched');
    expect(ehrWriteReasonLabel('ccda-document')).toBe('Summary of care document (C-CDA), not a note: not sent');
  });

  it('names why an eClinicalWorks or athena record waits', () => {
    expect(ehrWriteReasonLabel('vendor-activation-required'))
      .toBe("Dry run: the EHR vendor's write APIs are not marked as activated on this connection");
    expect(ehrWriteReasonLabel('patient-awaiting-mpi'))
      .toBe('Patient not found by identifier; waiting for the master patient index to match them');
    expect(ehrWriteReasonLabel('holder-encounter-not-enabled'))
      .toBe('Needs a telephone encounter, which this destination does not create (turn it on to send)');
    expect(ehrWriteReasonLabel('missing-snomed-code')).toBe('No SNOMED code (athena files problems by SNOMED only)');
    expect(ehrWriteReasonLabel('not-completed')).toBe('Not completed');
  });

  it('falls back to the code in words for a code it does not know', () => {
    expect(ehrWriteReasonLabel('some-new-reason')).toBe('Some new reason');
  });
});

describe('ehrWriteScopeLabel', () => {
  it('describes each scope status', () => {
    expect(ehrWriteScopeLabel('verified')).toEqual({ text: 'Access verified for every type this run would write', ok: true });
    expect(ehrWriteScopeLabel('missing:Patient,Condition'))
      .toEqual({ text: 'The EHR app is missing access for: Patient, Condition', ok: false });
    expect(ehrWriteScopeLabel('unknown').ok).toBeFalse();
  });
});

describe('ehrWriteReportToText', () => {
  it('copies the totals and every resource type with its reasons', () => {
    const text = ehrWriteReportToText(parseEhrWriteReport(DRY_RUN_DETAIL)!);

    expect(text).toContain('Dry run (clone mode) to Epic');
    expect(text).toContain('Would write: 1');
    expect(text).toContain('Condition: 35 received, 35 skipped');
    expect(text).toContain('31 × Not a problem-list entry (the EHR accepts problems only) (not-a-problem-list-item)');
  });
});

describe('a test run on a Generic FHIR server', () => {
  const detail = JSON.stringify({
    EhrWrite: {
      DryRun: false, CloneMode: false, TestRun: true, TargetVendor: 'Athenahealth', RecordsReceived: 2, ScopeStatus: 'test-server',
      Resources: [{ ResourceType: 'Observation', Received: 2, WouldWrite: 0, Written: 1, AlreadyWritten: 0, Skipped: 1, Rejected: 0, Unknown: 0,
        Reasons: { 'variant-not-enabled': 1 } }],
    },
  });

  it('is read as a test run whose vendor access is not checked', () => {
    const report = parseEhrWriteReport(detail)!;

    expect(report.TestRun).toBeTrue();
    expect(ehrWriteScopeLabel(report.ScopeStatus).ok).toBeTrue();
    expect(ehrWriteReportToText(report)).toContain('Live run (test run on a Generic FHIR server) to Athenahealth');
    expect(ehrWriteReasonLabel('variant-not-enabled')).toContain('has not turned on');
    expect(ehrWriteReasonLabel('target-references-unmappable')).toContain('CSV / SQL Table');
  });

  it('is not a test run when an older report says nothing', () => {
    expect(parseEhrWriteReport(DRY_RUN_DETAIL)!.TestRun).toBeFalse();
  });
});
