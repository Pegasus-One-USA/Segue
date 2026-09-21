/**
 * Maps a SystemSetting key to the edit control it should use in the settings dialog, instead of
 * the generic free-text `value` field — reused for every terminology code automatically, since
 * it's purely a function of the key pattern, not a per-code component.
 *
 * Mirrors the backend's per-code Frequency support (see TerminologySyncScheduleConfig usages in
 * src/Worker/FHIRBridge.Worker/*TerminologySyncWorker.cs / *SynchronizationWorker.cs). RxNorm and
 * Snomed intentionally have no entry here — their sync cadence is a fixed external release
 * schedule, not a Frequency setting, so no such key will ever exist for them.
 */

export type TerminologyFrequencyOption = 'Daily' | 'Weekly' | 'Monthly';

export type SettingFieldDescriptor =
  | { kind: 'toggle' }
  | { kind: 'frequency'; options: TerminologyFrequencyOption[] }
  | { kind: 'select'; options: string[] }
  | { kind: 'time' }
  | { kind: 'text' };

/** Fixed-choice General Settings keys, rendered as a dropdown instead of the free-text default.
 *
 *  Generalizes what TERMINOLOGY_FREQUENCY_OPTIONS does for terminology keys: without an entry here a key
 *  falls through to `text`, where a typo ("Dayly") saves happily and silently breaks the consumer that
 *  parses it. Keyed by exact setting key, since these are one-off enumerations rather than a pattern. */
const SELECT_OPTIONS: Record<string, string[]> = {
  // Mirrors the backend WorkflowNumberResetPolicy enum — WorkflowNumberGenerator parses this value by name.
  'WorkflowNumbering:ResetPolicy': ['Never', 'Daily', 'Monthly', 'Quarterly', 'Yearly', 'CustomAnchorDate'],
};

const TERMINOLOGY_FREQUENCY_OPTIONS: Record<string, TerminologyFrequencyOption[]> = {
  CvxHapi: ['Weekly', 'Monthly'],
  DcmHapi: ['Weekly', 'Monthly'],
  HcpcsHapi: ['Weekly', 'Monthly'],
  Icd10Hapi: ['Weekly', 'Monthly'],
  Icd10PcsHapi: ['Weekly', 'Monthly'],
  Icd11Hapi: ['Weekly', 'Monthly'],
  Icpc3Hapi: ['Weekly', 'Monthly'],
  LoincHapi: ['Weekly', 'Monthly'],
  MeshHapi: ['Weekly', 'Monthly'],
  NdcHapi: ['Weekly', 'Monthly'],
  RxNormHapi: ['Weekly', 'Monthly'],
  SnomedHapi: ['Weekly', 'Monthly'],
  UcumHapi: ['Weekly', 'Monthly'],
  Loinc: ['Weekly', 'Monthly'],
  Ndc: ['Daily', 'Weekly'],
  Ucum: ['Weekly', 'Monthly'],
  // RxNorm, Snomed: intentionally absent — fixed external release cadence, no Frequency setting.
};

const TERMINOLOGY_FREQUENCY_KEY = /^Terminology:([A-Za-z0-9]+):Frequency$/;
const TERMINOLOGY_EXECUTION_TIME_KEY = /^Terminology:[A-Za-z0-9]+:ExecutionTime$/;

/** Human-readable label for a terminology code segment (the "CvxHapi" in "Terminology:CvxHapi:Frequency"),
 * used to group that code's settings under one collapsible heading in the settings list. */
const TERMINOLOGY_CODE_LABELS: Record<string, string> = {
  CvxHapi: 'CVX (HAPI terminology server)',
  DcmHapi: 'DCM (HAPI terminology server)',
  HcpcsHapi: 'HCPCS (HAPI terminology server)',
  Icd10Hapi: 'ICD-10-CM (HAPI terminology server)',
  Icd10PcsHapi: 'ICD-10-PCS (HAPI terminology server)',
  Icd11Hapi: 'ICD-11 MMS (HAPI terminology server)',
  Icpc3Hapi: 'ICPC-3 (HAPI terminology server)',
  LoincHapi: 'LOINC (HAPI terminology server)',
  MeshHapi: 'MeSH (HAPI terminology server)',
  NdcHapi: 'NDC (HAPI terminology server)',
  RxNormHapi: 'RxNorm (HAPI terminology server)',
  SnomedHapi: 'SNOMED CT (HAPI terminology server)',
  UcumHapi: 'UCUM (HAPI terminology server)',
  Loinc: 'LOINC',
  Ndc: 'NDC',
  RxNorm: 'RxNorm',
  Snomed: 'SNOMED CT',
  Ucum: 'UCUM',
};

const TERMINOLOGY_CODE_KEY = /^Terminology:([A-Za-z0-9]+):[A-Za-z0-9]+$/;

/** The code segment of a per-code terminology setting key, or null for a key with no code
 * segment (e.g. "Terminology:BaseUrl", shared across every code) or an unrecognized code. */
export function terminologyCodeOf(key: string): { code: string; label: string } | null {
  const match = TERMINOLOGY_CODE_KEY.exec(key);
  if (!match) return null;
  const label = TERMINOLOGY_CODE_LABELS[match[1]];
  return label ? { code: match[1], label } : null;
}

/** "AnomalyDetection" → "Anomaly Detection" — no curated map needed for General Settings'
 * prefixes, unlike terminology codes (e.g. "CvxHapi"), since they're already readable words. */
function humanizeSegment(segment: string): string {
  return segment.replace(/([a-z0-9])([A-Z])/g, '$1 $2');
}

/** The leading `Segment:` prefix of a non-terminology setting key (e.g. "AnomalyDetection" from
 * "AnomalyDetection:MinBaselineRuns"), used to group General Settings the same way Terminology
 * Settings groups by code. Returns null for a key with no `:` at all (nothing to group by). */
export function generalSettingGroupOf(key: string): { code: string; label: string } | null {
  const colonIndex = key.indexOf(':');
  if (colonIndex < 0) return null;
  const segment = key.slice(0, colonIndex);
  return { code: segment, label: humanizeSegment(segment) };
}

export function describeSettingField(key: string): SettingFieldDescriptor {
  if (key.endsWith(':SchedulerEnabled') || key.endsWith('Enabled')) {
    return { kind: 'toggle' };
  }

  const selectOptions = SELECT_OPTIONS[key];
  if (selectOptions) {
    return { kind: 'select', options: selectOptions };
  }

  const frequencyMatch = TERMINOLOGY_FREQUENCY_KEY.exec(key);
  if (frequencyMatch) {
    const options = TERMINOLOGY_FREQUENCY_OPTIONS[frequencyMatch[1]];
    if (options) {
      return { kind: 'frequency', options };
    }
  }

  if (TERMINOLOGY_EXECUTION_TIME_KEY.test(key)) {
    return { kind: 'time' };
  }

  return { kind: 'text' };
}

/** "WorkflowNumbering:PadWidth" -> "Pad Width". The group row already names the group, so repeating the
 *  prefix on every field inside its own dialog is noise; the raw key stays available as the input's id. */
export function settingFieldLabel(key: string): string {
  const colonIndex = key.lastIndexOf(':');
  const tail = colonIndex >= 0 ? key.slice(colonIndex + 1) : key;
  return humanizeSegment(tail);
}
