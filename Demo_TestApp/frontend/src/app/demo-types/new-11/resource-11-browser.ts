import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, input, output, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { firstValueFrom } from 'rxjs';
import { RESOURCE_11_MENU, Resource11Column, Resource11MenuItem } from './core/config/resource-11-menu.config';
import { Resource11Service } from './core/services/resource-11.service';

// Shared "New 11" data browser: a resource selector (the 11 curated _11 tables) + a table that shows every row of
// the selected table, exactly as fetched from /api/v11/{apiSegment}. Fully driven by RESOURCE_11_MENU, so the same
// component serves every non-Admin role's "New 11" menu — the per-role wrappers just embed it.
@Component({
  selector: 'app-resource-11-browser',
  standalone: true,
  imports: [MatIconModule, MatProgressSpinnerModule],
  providers: [DatePipe],
  templateUrl: './resource-11-browser.html',
  styleUrl: './resource-11-browser.scss',
})
export class Resource11BrowserComponent implements OnInit {
  readonly loginTypeLabel = input('');
  readonly logout = output<void>();

  readonly menu = RESOURCE_11_MENU;
  readonly selected = signal<Resource11MenuItem>(RESOURCE_11_MENU[0]);

  readonly rows = signal<Record<string, unknown>[]>([]);
  readonly isLoading = signal(true);
  readonly loadError = signal<string | null>(null);

  private readonly resource11 = inject(Resource11Service);
  private readonly datePipe = inject(DatePipe);

  ngOnInit(): void {
    void this.loadRows();
  }

  selectResource(item: Resource11MenuItem): void {
    if (this.selected().key === item.key) {
      return;
    }
    this.selected.set(item);
    void this.loadRows();
  }

  private async loadRows(): Promise<void> {
    this.isLoading.set(true);
    this.loadError.set(null);

    try {
      const rows = await firstValueFrom(this.resource11.getRows(this.selected()));
      this.rows.set(rows ?? []);
    } catch {
      this.loadError.set(`Could not load ${this.selected().label} records. Make sure the _11 tables exist and are populated.`);
      this.rows.set([]);
    } finally {
      this.isLoading.set(false);
    }
  }

  // Turns a raw cell value into display text: '-' for missing, Yes/No for booleans, localized dates for date/
  // datetime columns, and the value itself otherwise. Keeps every null-handling/formatting decision in one place.
  formatCell(row: Record<string, unknown>, col: Resource11Column): string {
    const value = row[col.key];
    if (value === null || value === undefined || value === '') {
      return '-';
    }

    switch (col.type) {
      case 'date':
        return this.datePipe.transform(value as string, 'mediumDate') ?? String(value);
      case 'datetime':
        return this.datePipe.transform(value as string, 'medium') ?? String(value);
      case 'boolean':
        return value ? 'Yes' : 'No';
      default:
        return String(value);
    }
  }
}
