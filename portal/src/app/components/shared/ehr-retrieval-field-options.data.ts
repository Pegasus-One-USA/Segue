/**
 * Pure data/utility module: the retrieval-schedule option lists and time-zone helper originally defined inside
 * the old shared EhrVendorSourceFormComponent engine (removed — each EHR vendor source form, e.g.
 * EpicSourceFormComponent/AthenahealthSourceFormComponent/etc., is now a fully independent component with this
 * same content duplicated inline). Kept here as a standalone, non-component data file purely so
 * GenericFhirSourceFormComponent (which shares the same calendar-recurrence scheduling model but is not one of
 * the EHR vendor forms) has a real, importable home for these constants without depending on any one vendor's
 * component file. Nothing in this file renders UI or drives multiple sources' behavior — it's the kind of
 * reusable constant/utility that's explicitly fine to share.
 */

export interface RetrievalFieldOption { value: string; label: string; }

/** Browser's own zone (e.g. "America/New_York") — used as the schedule time zone picker's default. */
export function detectBrowserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  } catch {
    return 'UTC';
  }
}

/** All IANA zone identifiers the runtime knows about, for the "Time zone" select. Falls back to a short curated
 *  list on engines without `Intl.supportedValuesOf` (older Safari/older browsers not in FHIRBridge's support matrix
 *  but cheap to guard against). */
export const TIME_ZONE_OPTIONS: readonly RetrievalFieldOption[] = (() => {
  const zones: string[] = typeof Intl.supportedValuesOf === 'function'
    ? Intl.supportedValuesOf('timeZone')
    : ['UTC', 'America/New_York', 'America/Chicago', 'America/Denver', 'America/Los_Angeles', 'Europe/London'];
  return zones.map(zone => ({ value: zone, label: zone.replace(/_/g, ' ') }));
})();

export const POLL_FREQUENCY_OPTIONS: readonly RetrievalFieldOption[] = [
  { value: '5m',  label: 'Every 5 minutes' },
  { value: '15m', label: 'Every 15 minutes' },
  { value: '30m', label: 'Every 30 minutes' },
  { value: '1h',  label: 'Hourly' },
  { value: '1d',  label: 'Daily' },
];

export const RECONCILIATION_OPTIONS: readonly RetrievalFieldOption[] = [
  { value: 'none', label: 'Disabled' },
  { value: '1h',   label: 'Hourly' },
  { value: '6h',   label: 'Every 6 hours' },
  { value: '1d',   label: 'Daily' },
  { value: '1w',   label: 'Weekly' },
];

export const ENDPOINT_TYPE_OPTIONS: readonly RetrievalFieldOption[] = [
  { value: 'rest-hook', label: 'REST Hook (HTTPS callback)' },
  { value: 'websocket', label: 'WebSocket' },
  { value: 'mllp',      label: 'MLLP (HL7 v2)' },
];

export const EVENT_TYPE_OPTIONS: readonly RetrievalFieldOption[] = [
  { value: 'created',            label: 'Record created' },
  { value: 'updated',            label: 'Record updated' },
  { value: 'created-or-updated', label: 'Created or updated' },
  { value: 'deleted',            label: 'Record deleted' },
];

export const SORT_OPTIONS: readonly RetrievalFieldOption[] = [
  { value: '_lastUpdated',  label: '_lastUpdated (oldest → newest)' },
  { value: '-_lastUpdated', label: '_lastUpdated (newest → oldest)' },
  { value: 'date',          label: 'date (ascending)' },
  { value: '-date',         label: 'date (descending)' },
];

export const RETRY_POLICY_OPTIONS: readonly RetrievalFieldOption[] = [
  { value: 'none',        label: 'No retry' },
  { value: 'fixed-3',     label: 'Fixed — 3 attempts' },
  { value: 'exponential', label: 'Exponential backoff' },
];

// ── Full Refresh calendar recurrence (Google Calendar-style: anchored to a specific time, not an interval) ──────
export const FULL_REFRESH_RECURRENCE_OPTIONS: readonly RetrievalFieldOption[] = [
  { value: 'daily',   label: 'Daily' },
  { value: 'weekly',  label: 'Weekly' },
  { value: 'monthly', label: 'Monthly' },
];

// cron day-of-week: 0 = Sunday … 6 = Saturday.
export const WEEKDAY_OPTIONS: readonly RetrievalFieldOption[] = [
  { value: '0', label: 'Sun' }, { value: '1', label: 'Mon' }, { value: '2', label: 'Tue' },
  { value: '3', label: 'Wed' }, { value: '4', label: 'Thu' }, { value: '5', label: 'Fri' }, { value: '6', label: 'Sat' },
];
