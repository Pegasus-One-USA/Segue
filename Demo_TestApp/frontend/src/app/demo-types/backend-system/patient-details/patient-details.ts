import { DatePipe } from '@angular/common';
import { Component, effect, input, output, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTableModule } from '@angular/material/table';
import { firstValueFrom } from 'rxjs';
import { BackendSystemService, PatientDataSource } from '../core/services/backend-system.service';
import { BACKEND_SYSTEM_MENU, ResourceMenuItem } from '../core/config/backend-system-menu.config';

@Component({
  selector: 'app-patient-details',
  standalone: true,
  imports: [DatePipe, MatIconModule, MatProgressSpinnerModule, MatTableModule],
  templateUrl: './patient-details.html',
  styleUrl: './patient-details.scss',
})
export class PatientDetailsComponent {
  readonly patientId = input.required<string>();
  readonly patientName = input<string | null>(null);
  // Only the "Patient" menu item's own detail fetch honors this — every other tab (Encounters, Observations, ...)
  // always reads SQL Server, unaffected by the Patient List page's Data Source radio group.
  readonly dataSource = input<PatientDataSource>('sql');
  readonly back = output<void>();

  // Not hardcoded into the template — this list drives the left nav and, per menu item, which columns the table
  // renders. Swapping this constant for an HTTP-loaded signal later (once BackendSystem metadata tables exist,
  // per requirement section 10) requires no change here beyond where the array's value comes from.
  readonly menu: ResourceMenuItem[] = BACKEND_SYSTEM_MENU;

  readonly selectedMenuKey = signal(this.menu[0]?.key ?? '');
  readonly isLoading = signal(true);
  readonly loadError = signal<string | null>(null);
  readonly rows = signal<Record<string, unknown>[]>([]);

  constructor(private readonly backendSystem: BackendSystemService) {
    // Re-runs on both patientId changes (input.required, could still change identity across navigations) and
    // selectedMenuKey changes — one effect covers both triggers instead of duplicating the load call in two places.
    effect(() => {
      const patientId = this.patientId();
      const menuKey = this.selectedMenuKey();
      void this.loadResource(patientId, menuKey);
    });
  }

  selectMenu(menuKey: string): void {
    this.selectedMenuKey.set(menuKey);
  }

  displayedColumns(): string[] {
    return this.selectedMenu()?.columns.map((column) => column.key) ?? [];
  }

  selectedMenu(): ResourceMenuItem | undefined {
    return this.menu.find((item) => item.key === this.selectedMenuKey());
  }

  /** Null-safe cell accessor — every resource field can legitimately be missing (requirement section 12); this
   *  never throws even if a row is missing the property entirely. */
  cellValue(row: Record<string, unknown>, key: string): unknown {
    return row?.[key] ?? null;
  }

  /** Same null-safety as cellValue, narrowed to what DatePipe accepts — the API only ever puts ISO date strings
   *  (or null) in date-typed columns, so this narrowing is safe rather than a blind cast. */
  dateCellValue(row: Record<string, unknown>, key: string): string | null {
    const value = this.cellValue(row, key);
    return typeof value === 'string' ? value : null;
  }

  booleanCellValue(row: Record<string, unknown>, key: string): boolean | null {
    const value = this.cellValue(row, key);
    return typeof value === 'boolean' ? value : null;
  }

  private async loadResource(patientId: string, menuKey: string): Promise<void> {
    const menuItem = this.menu.find((item) => item.key === menuKey);
    if (!patientId || !menuItem) {
      return;
    }

    this.isLoading.set(true);
    this.loadError.set(null);

    try {
      const rows = await firstValueFrom(this.backendSystem.getResourceRows(patientId, menuItem, this.dataSource()));
      this.rows.set(rows ?? []);
    } catch {
      this.loadError.set(`Could not load ${menuItem.label} for this patient.`);
      this.rows.set([]);
    } finally {
      this.isLoading.set(false);
    }
  }
}
