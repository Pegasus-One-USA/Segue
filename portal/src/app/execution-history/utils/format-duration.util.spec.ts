import { formatDuration } from './format-duration.util';

/** One formatter for the run's Duration (list and detail) and the popup's export Elapsed. Rounding only the seconds
 *  part used to read 59.6s as "60s" and 119.6s as "1m 60s". */
describe('formatDuration', () => {
  it('reads whole seconds under a minute', () => {
    expect(formatDuration(0)).toBe('0s');
    expect(formatDuration(45_400)).toBe('45s');
  });

  it('rounds to whole seconds before splitting, so no "60s" ever shows', () => {
    expect(formatDuration(59_400)).toBe('59s');
    expect(formatDuration(59_600)).toBe('1m 0s');
    expect(formatDuration(119_600)).toBe('2m 0s');
    expect(formatDuration(3_599_700)).toBe('60m 0s');
  });

  it('reads the run and the export of the reported eCW run as the screens showed them', () => {
    expect(formatDuration(126_568)).toBe('2m 7s');   // run: 12:05:24.924 -> 12:07:31.492
    expect(formatDuration(85_861)).toBe('1m 26s');   // export: 12:05:37.139 -> 12:07:03
  });

  it('reads no usable interval as a dash', () => {
    expect(formatDuration(null)).toBe('—');
    expect(formatDuration(undefined)).toBe('—');
    expect(formatDuration(Number.NaN)).toBe('—');
    expect(formatDuration(-1)).toBe('—');
  });
});
