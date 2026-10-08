import { Component, input, output } from '@angular/core';
import { TabularSourceFile, TabularStreamCheck } from '../../../../services/tabular-source.service';
import { TabularEntry } from '../tabular-entry.model';

/**
 * One resource type of a CSV / SQL Table source: its query (database) or file and row filter (CSV), its template,
 * and what the last check said about it. Edits go up as a partial entry; the host owns the list.
 */
@Component({
  selector: 'app-tabular-stream-card',
  standalone: true,
  templateUrl: './tabular-stream-card.component.html',
  styleUrl: '../tabular-form-shared.scss',
})
export class TabularStreamCardComponent {
  readonly entry = input.required<TabularEntry>();
  readonly index = input.required<number>();
  readonly kind = input.required<'csv' | 'sql'>();
  readonly files = input<TabularSourceFile[]>([]);
  /** The last check of exactly the current settings, or null. */
  readonly check = input<TabularStreamCheck | null>(null);

  readonly changed = output<Partial<TabularEntry>>();
  readonly resetTemplate = output<void>();

  fileColumns(fileId: string): string[] {
    return this.files().find(f => f.id === fileId)?.columns ?? [];
  }

  set(field: keyof TabularEntry, value: string): void {
    this.changed.emit({ [field]: value });
  }

  prettyJson(json: string): string {
    try {
      return JSON.stringify(JSON.parse(json), null, 2);
    } catch {
      return json;
    }
  }
}
