/** Matches the backend's EhrWriteSummary / EhrWriteResourceCounts (PascalCase), stored by
 *  EfWorkflowNodeResourceHistoryRecorder.SummarizeDelivery under the "EhrWrite" key of a destination node's
 *  delivery detail. Counts and reason codes only: no resource content, so it is safe to show and to copy. */
export interface EhrWriteReport {
  DryRun: boolean;
  CloneMode: boolean;
  TargetVendor: string;
  RecordsReceived: number;
  /** "verified", "missing:<types>" or "unknown". */
  ScopeStatus: string;
  Resources: EhrWriteResourceCounts[];
}

export interface EhrWriteResourceCounts {
  ResourceType: string;
  Received: number;
  WouldWrite: number;
  Written: number;
  AlreadyWritten: number;
  Skipped: number;
  Rejected: number;
  Unknown: number;
  Reasons: Record<string, number>;
}

/** The outcome columns, in the order the report shows them. */
export const EHR_WRITE_OUTCOMES = [
  { key: 'WouldWrite', label: 'Would write' },
  { key: 'Written', label: 'Written' },
  { key: 'AlreadyWritten', label: 'Already written' },
  { key: 'Skipped', label: 'Skipped' },
  { key: 'Rejected', label: 'Rejected' },
  { key: 'Unknown', label: 'Unknown' },
] as const;

export type EhrWriteOutcomeKey = (typeof EHR_WRITE_OUTCOMES)[number]['key'];

/** The EHR write-back report inside a destination node's delivery detail, or null when the node is not an EHR
 *  write-back destination, or the run predates the report. Never throws: a malformed payload shows no report. */
export function parseEhrWriteReport(deliveryDetailJson: string | null | undefined): EhrWriteReport | null {
  if (!deliveryDetailJson) return null;

  let detail: unknown;
  try {
    detail = JSON.parse(deliveryDetailJson);
  } catch {
    return null;
  }

  const raw = (detail as { EhrWrite?: unknown } | null)?.EhrWrite as Partial<EhrWriteReport> | null | undefined;
  if (!raw || typeof raw !== 'object' || !Array.isArray(raw.Resources)) return null;

  return {
    DryRun: raw.DryRun === true,
    CloneMode: raw.CloneMode === true,
    TargetVendor: typeof raw.TargetVendor === 'string' ? raw.TargetVendor : '',
    RecordsReceived: count(raw.RecordsReceived),
    ScopeStatus: typeof raw.ScopeStatus === 'string' ? raw.ScopeStatus : 'unknown',
    Resources: raw.Resources
      .filter((r): r is EhrWriteResourceCounts => !!r && typeof r === 'object' && typeof r.ResourceType === 'string')
      .map(r => ({
        ResourceType: r.ResourceType,
        Received: count(r.Received),
        WouldWrite: count(r.WouldWrite),
        Written: count(r.Written),
        AlreadyWritten: count(r.AlreadyWritten),
        Skipped: count(r.Skipped),
        Rejected: count(r.Rejected),
        Unknown: count(r.Unknown),
        Reasons: reasons(r.Reasons),
      })),
  };
}

/** Each outcome summed over every resource type. */
export function ehrWriteTotals(report: EhrWriteReport): Record<EhrWriteOutcomeKey, number> {
  const totals = { WouldWrite: 0, Written: 0, AlreadyWritten: 0, Skipped: 0, Rejected: 0, Unknown: 0 };
  for (const r of report.Resources) {
    for (const { key } of EHR_WRITE_OUTCOMES) totals[key] += r[key];
  }
  return totals;
}

/** A resource type's reason codes, most frequent first. */
export function ehrWriteReasons(resource: EhrWriteResourceCounts): { code: string; label: string; count: number }[] {
  return Object.entries(resource.Reasons)
    .filter(([, n]) => n > 0)
    .sort(([a, x], [b, y]) => y - x || a.localeCompare(b))
    .map(([code, n]) => ({ code, label: ehrWriteReasonLabel(code), count: n }));
}

/** Plain wording for the scope check: whether the EHR's token covers the types the run writes. */
export function ehrWriteScopeLabel(scopeStatus: string): { text: string; ok: boolean } {
  if (scopeStatus === 'verified') return { text: 'Access verified for every type this run would write', ok: true };
  if (scopeStatus.startsWith('missing:')) {
    return { text: `The EHR app is missing access for: ${scopeStatus.slice('missing:'.length).split(',').join(', ')}`, ok: false };
  }
  return { text: 'Access could not be checked', ok: false };
}

/** Reason codes written by MappedEhrWriteBackDestinationWriter, EhrReferenceResolver and the vendor write
 *  profiles, in the words an analyst deciding whether to go live would use. Unlisted codes fall back to the
 *  code itself with dashes turned into spaces, so a new code still reads sensibly. */
const REASON_LABELS: Record<string, string> = {
  // Run-level and release
  // Recorded both when the source is the target EHR itself and when the EHR answers a create with "duplicate".
  'already-in-ehr': 'Already in the EHR (read from it, or it reported a duplicate)',
  'live-write-not-supported': 'This EHR accepts dry runs only for this type',
  // Recorded only by runs before the live-write release setting was removed.
  'live-write-not-released': 'Type was not released for live writes (older run)',
  'write-cap-reached': 'Max writes per run reached',
  'not-selected': 'Type not selected in Data groups',
  'not-writable': 'This EHR does not accept this type',
  'not-a-fhir-resource': 'Not a FHIR resource',
  'missing-id': 'Source record has no id',
  // Ledger
  'already-written': 'Written in an earlier run',
  'changed-after-write': 'Changed in the source after it was written (not resent)',
  'previously-rejected': 'Refused by the EHR before and unchanged since',
  'awaiting-review': 'Being sent by another run, or waiting in EHR Write-Back Review',
  'outcome-unknown': 'Sent, but the result is unknown',
  'rejected-by-ehr': 'Refused by the EHR',
  'clone-matched-original': 'The EHR matched the clone to the real patient',
  // Patients
  'patient-unresolved': 'Patient could not be found in the EHR',
  'patient-not-in-ehr': 'Patient not in the EHR (create is off)',
  'patient-not-matched': 'Patient not found by identifier',
  'patient-match-needs-review': 'Uncertain patient match, left for review',
  'patient-match-failed': 'Patient match failed',
  'patient-identifier-ambiguous': 'Identifier matches more than one patient',
  'patient-not-yet-created': 'Waiting for the patient to be created',
  'patient-not-selected': 'Patient not selected in Data groups, so the patient cannot be created',
  'patient-not-created': 'Patient could not be created',
  'patient-created': 'Patient created earlier in this run',
  'patient-already-in-ehr': 'Patient already in the EHR',
  'patient-awaiting-review': 'Patient write waiting for review',
  'patient-not-writable': 'This EHR does not accept new patients',
  'patient-reference-invalid': 'Record does not point to a valid patient',
  'source-patient-unavailable': 'Patient missing from the source data',
  'missing-patient': 'Record has no patient',
  'deceased-patient': 'Patient is deceased',
  'patient-missing-name': 'Patient has no name',
  'patient-missing-gender': 'Patient has no gender',
  'patient-missing-birth-date': 'Patient has no birth date',
  'patient-deceased-patient': 'Patient is deceased',
  // Visits
  'no-eligible-encounter': 'No suitable visit for this patient in the EHR',
  // Allergies and problems
  'not-active': 'Not active',
  'entered-in-error': 'Entered in error',
  'no-known-allergies': '"No known allergies" entry',
  'not-a-problem-list-item': 'Not a problem-list entry (the EHR accepts problems only)',
  'text-only-problem': 'Problem has text but no code',
  'missing-problem-name': 'Problem has no name',
  'missing-code': 'No code',
  // Vitals
  'not-a-vital-sign': 'Not a vital sign',
  'vital-signs-panel': 'A vitals panel, not a single reading',
  'not-final': 'Not final',
  'missing-value': 'No value',
  'missing-unit': 'No unit',
  'missing-loinc-code': 'No LOINC code',
  'missing-effective-time': 'No time taken',
  'effective-without-time': 'Has a date but no time',
  'effective-without-timezone': 'Time has no time zone',
  'blood-pressure-missing-component': 'Blood pressure missing a reading',
  'unable-to-assess': 'Marked unable to assess',
  // Notes
  'not-a-clinical-note': 'Not a clinical note',
  'excluded-note-type': 'Note type not sent (for example discharge instructions)',
  'missing-note-type': 'Note has no type',
  'missing-note-content': 'Note has no content',
  'note-content-not-inline': 'Note content is a link, not attached',
  'note-content-not-base64': 'Note content is not valid base64',
  'note-format-not-supported': 'Note format not supported (plain text, HTML or RTF only)',
  'note-empty-after-conversion': 'Note is empty after conversion to text',
  // Questionnaires
  'not-completed': 'Questionnaire not completed',
  'in-progress': 'In progress',
  'missing-questionnaire': 'No questionnaire reference',
  'missing-answers': 'No answers',
};

export function ehrWriteReasonLabel(code: string): string {
  const known = REASON_LABELS[code];
  if (known) return known;
  const words = code.replace(/[-_]+/g, ' ').trim();
  return words ? words.charAt(0).toUpperCase() + words.slice(1) : code;
}

/** The report as plain text, for the row's Copy button. */
export function ehrWriteReportToText(report: EhrWriteReport): string {
  const totals = ehrWriteTotals(report);
  const lines = [
    `${report.DryRun ? 'Dry run' : 'Live run'}${report.CloneMode ? ' (clone mode)' : ''} to ${report.TargetVendor || 'EHR'}`,
    `Records received: ${report.RecordsReceived}`,
    ...EHR_WRITE_OUTCOMES.map(o => `${o.label}: ${totals[o.key]}`),
    `Access: ${ehrWriteScopeLabel(report.ScopeStatus).text}`,
  ];
  for (const r of report.Resources) {
    lines.push('', `${r.ResourceType}: ${r.Received} received, ` +
      EHR_WRITE_OUTCOMES.filter(o => r[o.key] > 0).map(o => `${r[o.key]} ${o.label.toLowerCase()}`).join(', '));
    for (const reason of ehrWriteReasons(r)) lines.push(`  ${reason.count} × ${reason.label} (${reason.code})`);
  }
  return lines.join('\n');
}

function count(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) && value > 0 ? Math.floor(value) : 0;
}

function reasons(value: unknown): Record<string, number> {
  if (!value || typeof value !== 'object') return {};
  const result: Record<string, number> = {};
  for (const [code, n] of Object.entries(value as Record<string, unknown>)) {
    const c = count(n);
    if (c > 0) result[code] = c;
  }
  return result;
}
