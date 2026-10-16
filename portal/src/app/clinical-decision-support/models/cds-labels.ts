import { CdsCallKind, CdsCallOutcome, CdsTestOutcome } from './cds.models';

/** Plain names for the FHIR resources a fix creates or deletes. */
const FIX_RESOURCE_LABELS: Record<string, string> = {
  MedicationRequest: 'medication order',
  ServiceRequest: 'test or procedure order',
  ProcedureRequest: 'procedure order',
  Condition: 'problem or diagnosis',
};

export function cdsFixResourceLabel(resourceType: string | null | undefined): string {
  if (!resourceType) return 'record';
  return FIX_RESOURCE_LABELS[resourceType] ?? resourceType;
}

/** What a fix does in the EHR, e.g. "Removes a draft medication order". */
export function cdsFixActionLabel(fix: { actionType: string; resourceType: string }): string {
  const what = cdsFixResourceLabel(fix.resourceType);
  if (fix.actionType === 'delete') return `Removes a draft ${what}`;
  return fix.resourceType === 'Condition' ? `Adds a ${what}` : `Adds a draft ${what}`;
}

/** Plain-language names for the CDS Hooks hook ids the handlers support. Unknown hooks show their raw id. */
const HOOK_LABELS: Record<string, string> = {
  'patient-view': 'Patient chart opened',
  'order-select': 'Order selected',
  'order-sign': 'Order signed',
  'order-dispatch': 'Order sent',
  'encounter-start': 'Visit started',
  'encounter-discharge': 'Patient discharged',
  'appointment-book': 'Appointment booked',
};

export function cdsHookLabel(hook: string | null | undefined): string {
  if (!hook) return '—';
  return HOOK_LABELS[hook] ?? hook;
}

const OUTCOME_LABELS: Record<CdsCallOutcome, string> = {
  Success: 'Cards returned',
  NoCards: 'No cards',
  Degraded: 'Missing data',
  PreconditionFailed: 'Missing data (412)',
  Unauthorized: 'Not signed in',
  Forbidden: 'Not allowed',
  NotFound: 'Unknown service',
  BadRequest: 'Bad request',
  Error: 'Error',
};

export const CDS_CALL_OUTCOMES = Object.keys(OUTCOME_LABELS) as CdsCallOutcome[];

/**
 * Plain-language outcome. Pass the call's kind for Activity rows: an accepted feedback call is also recorded as
 * Success, but it returned no cards.
 */
export function cdsOutcomeLabel(outcome: CdsCallOutcome | CdsTestOutcome, kind?: CdsCallKind): string {
  if (kind === 'Feedback' && outcome === 'Success') return 'Feedback received';
  return OUTCOME_LABELS[outcome] ?? outcome;
}

export type CdsOutcomeTone = 'success' | 'neutral' | 'warning' | 'error';

/** Colour family of an outcome badge: green when cards came back, grey when the service had nothing to say. */
export function cdsOutcomeTone(outcome: CdsCallOutcome | CdsTestOutcome): CdsOutcomeTone {
  if (outcome === 'Success') return 'success';
  if (outcome === 'NoCards') return 'neutral';
  if (outcome === 'Degraded' || outcome === 'PreconditionFailed') return 'warning';
  return 'error';
}
