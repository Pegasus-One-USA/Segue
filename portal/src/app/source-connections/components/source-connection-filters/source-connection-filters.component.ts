import { Component, input, output } from '@angular/core';

/** The raw select values; sourceFilterKeys turns them into the row filter keys. */
export interface SourceConnectionFilterValue {
  kind: '' | 'ehr-read' | 'database';
  vendor: string;
  audience: string;
  status: '' | 'true' | 'false';
}

export const EMPTY_SOURCE_FILTERS: SourceConnectionFilterValue = { kind: '', vendor: '', audience: '', status: '' };

/** An EHR or audience choice only matches EHR rows, since database rows carry no vendor or audience key. */
export function sourceFilterKeys(value: SourceConnectionFilterValue): string[] {
  return [
    value.kind ? `kind:${value.kind}` : '',
    value.vendor ? `vendor:${value.vendor}` : '',
    value.audience ? `audience:${value.audience}` : '',
  ].filter(k => !!k);
}

/**
 * The Source Connections filters: kind (EHR / Database — only when the role can list both), EHR, audience and status.
 * Option values stay the raw vendor / ApplicationType names (the e2e tests select by them).
 */
@Component({
  selector: 'app-source-connection-filters',
  standalone: true,
  templateUrl: './source-connection-filters.component.html',
  styleUrl: './source-connection-filters.component.scss',
})
export class SourceConnectionFiltersComponent {
  readonly value = input.required<SourceConnectionFilterValue>();
  readonly showKind = input(false);
  readonly ehrOptions = input<{ value: string; label: string }[]>([]);
  readonly audienceOptions = input<{ value: string; label: string }[]>([]);

  readonly valueChange = output<SourceConnectionFilterValue>();

  /** Database rows have no EHR, audience or status, so choosing Database clears and disables all three. */
  onKind(kind: string): void {
    const next = kind as SourceConnectionFilterValue['kind'];
    this.valueChange.emit(next === 'database'
      ? { kind: next, vendor: '', audience: '', status: '' }
      : { ...this.value(), kind: next });
  }

  onVendor(vendor: string): void {
    this.valueChange.emit({ ...this.value(), vendor });
  }

  onAudience(audience: string): void {
    this.valueChange.emit({ ...this.value(), audience });
  }

  onStatus(status: string): void {
    this.valueChange.emit({ ...this.value(), status: status as SourceConnectionFilterValue['status'] });
  }
}
