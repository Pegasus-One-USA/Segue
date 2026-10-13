/** A duration as Execution History shows it everywhere — the run's Duration (list and detail) and the Bulk Data
 *  Status popup's export Elapsed — so the same number of milliseconds always reads the same: "45s", "2m 7s".
 *
 *  Rounds to whole seconds FIRST, then splits into minutes and seconds. Rounding only the seconds part (the previous
 *  code) turned 59.6s into "60s" and 119.6s into "1m 60s", and made a ticking display skip "1m 0s" (59s -> 60s ->
 *  1m 1s). Null, NaN and negative input (no usable interval) read '—'. */
export function formatDuration(ms: number | null | undefined): string {
  if (ms === null || ms === undefined || Number.isNaN(ms) || ms < 0) return '—';
  const totalSeconds = Math.round(ms / 1000);
  if (totalSeconds < 60) return `${totalSeconds}s`;
  return `${Math.floor(totalSeconds / 60)}m ${totalSeconds % 60}s`;
}
