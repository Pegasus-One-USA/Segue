import { CdsCoverageImportAction, CdsCovered, CdsDocNeeded, CdsDocPurpose, CdsInfoNeeded, CdsPaNeeded } from './cds-coverage.models';

// Plain-language names for the Da Vinci CRD coverage-information codes, for rule tables, editors and Activity.

const COVERED: Record<string, string> = {
  covered: 'Covered',
  'not-covered': 'Not covered',
  conditional: 'Covered under conditions',
};

const PA_NEEDED: Record<string, string> = {
  'no-auth': 'No prior authorization',
  'auth-needed': 'Prior authorization needed',
  satisfied: 'Prior authorization already approved',
  performpa: 'Prior authorization to be done now',
  conditional: 'Prior authorization depends on details',
};

const DOC_NEEDED: Record<string, string> = {
  clinical: 'Clinical documentation',
  admin: 'Administrative documentation',
  both: 'Clinical and administrative documentation',
  conditional: 'Documentation depends on details',
};

const DOC_PURPOSE: Record<string, string> = {
  withpa: 'With the prior authorization request',
  withclaim: 'With the claim',
  withorder: 'With the order',
  'retain-doc': 'Keep on file',
  OTH: 'Other',
};

const INFO_NEEDED: Record<string, string> = {
  performer: 'Who performs it',
  location: 'Where it is done',
  timeframe: 'When it is done',
  'contract-window': 'The contract window',
  OTH: 'Other information',
};

const IMPORT_ACTION: Record<CdsCoverageImportAction, string> = {
  add: 'Add',
  update: 'Update',
  unchanged: 'No change',
  delete: 'Delete',
  error: 'Error',
};

export function cdsCoveredLabel(value: CdsCovered | string | null | undefined): string {
  if (!value) return 'Not stated';
  return COVERED[value] ?? value;
}

export function cdsPaNeededLabel(value: CdsPaNeeded | string | null | undefined): string {
  if (!value) return 'Not stated';
  return PA_NEEDED[value] ?? value;
}

export function cdsDocNeededLabel(value: CdsDocNeeded | string | null | undefined): string {
  if (!value) return 'No documentation';
  return DOC_NEEDED[value] ?? value;
}

export function cdsDocPurposeLabel(value: CdsDocPurpose | string): string {
  return DOC_PURPOSE[value] ?? value;
}

export function cdsInfoNeededLabel(value: CdsInfoNeeded | string): string {
  return INFO_NEEDED[value] ?? value;
}

export function cdsImportActionLabel(action: CdsCoverageImportAction | string): string {
  return IMPORT_ACTION[action as CdsCoverageImportAction] ?? action;
}

/**
 * One Activity decision ("covered/pa-needed/doc-needed", "-" = not stated) in plain words, e.g.
 * "auth-needed" → "Covered, prior authorization needed, clinical documentation".
 */
export function cdsCoverageDecisionLabel(decision: string): string {
  const [covered, pa, doc] = decision.split('/').map(part => part?.trim());
  const parts: string[] = [];
  parts.push(covered && covered !== '-' ? cdsCoveredLabel(covered) : 'Coverage not stated');
  if (pa && pa !== '-') parts.push(cdsPaNeededLabel(pa).toLowerCase());
  if (doc && doc !== '-') parts.push(cdsDocNeededLabel(doc).toLowerCase());
  return parts.join(', ');
}

/** Covered, prior authorization and documentation of a rule in one short line. */
export function cdsRuleSummary(rule: { covered: string; paNeeded: string | null; docNeeded: string | null }): string {
  return cdsCoverageDecisionLabel(`${rule.covered}/${rule.paNeeded ?? '-'}/${rule.docNeeded ?? '-'}`);
}
