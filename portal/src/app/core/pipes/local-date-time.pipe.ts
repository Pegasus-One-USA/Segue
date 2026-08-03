import { DatePipe } from '@angular/common';
import { LOCALE_ID, Pipe, PipeTransform, inject } from '@angular/core';

const OFFSET_PATTERN = /(?:[zZ]|[+-]\d{2}:?\d{2})$/;

/**
 * Every timestamp from the API is a UTC instant. Angular's built-in `date` pipe already
 * converts to the viewer's local timezone — but only when the value carries an explicit
 * UTC offset ("Z" or "+00:00"). Any value that slips through without one (e.g. a value
 * built client-side, or an older API response) would otherwise be silently misread as
 * already-local time. This pipe normalizes the input first so the conversion is never skipped.
 */
@Pipe({
  name: 'localDateTime',
  standalone: true,
  pure: true,
})
export class LocalDateTimePipe implements PipeTransform {
  private readonly datePipe = new DatePipe(inject(LOCALE_ID));

  transform(value: string | number | Date | null | undefined, format = 'MMM d, y, h:mm:ss a'): string | null {
    if (value === null || value === undefined || value === '') {
      return null;
    }

    const normalized = typeof value === 'string' && !OFFSET_PATTERN.test(value) ? `${value}Z` : value;
    return this.datePipe.transform(normalized, format);
  }
}
