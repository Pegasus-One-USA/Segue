import { Component, OnDestroy, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { MatIconModule } from '@angular/material/icon';
import { Subscription } from 'rxjs';
import { CdsApiService } from '../../services/cds-api.service';
import { CdsDiscoveryInfoService, CdsSetupSheet } from '../../models/cds.models';
import { cdsFixResourceLabel, cdsHookLabel } from '../../models/cds-labels';
import { CdsCopyButtonComponent } from '../cds-copy-button/cds-copy-button.component';
import { ToastService } from '../../../services/toast.service';

/**
 * The Epic setup sheet: Epic doesn't read the discovery URL, so the health system's Epic team enters every value by
 * hand. This lists them for one service (endpoint, audience, prefetch, issuer values, dismiss reason codes, fix codes
 * and SMART links to allow-list), as a readable table and as JSON to copy or download. Configuration only.
 */
@Component({
  selector: 'app-cds-epic-setup-sheet',
  standalone: true,
  imports: [DatePipe, MatIconModule, CdsCopyButtonComponent],
  templateUrl: './cds-epic-setup-sheet.component.html',
  styleUrl: './cds-epic-setup-sheet.component.scss',
})
export class CdsEpicSetupSheetComponent implements OnDestroy {
  private readonly api = inject(CdsApiService);
  private readonly toast = inject(ToastService);

  readonly services = input.required<CdsDiscoveryInfoService[]>();
  /** Service instance id to open first (e.g. from the service page's "Epic setup sheet" link). */
  readonly initialServiceId = input<string | null>(null);

  readonly selectedId = signal('');
  readonly loading = signal(false);
  readonly errorMessage = signal<string | null>(null);
  readonly sheet = signal<CdsSetupSheet | null>(null);

  readonly hookLabel = cdsHookLabel;
  readonly resourceLabel = cdsFixResourceLabel;

  readonly json = computed(() => {
    const sheet = this.sheet();
    return sheet ? JSON.stringify(sheet, null, 2) : '';
  });

  readonly smartLinks = computed(() => this.sheet()?.links.filter(l => l.type === 'smart') ?? []);

  private pending: Subscription | null = null;

  constructor() {
    // Opens the requested service, else the first one, once the list arrives. A new list (the page reloads it after
    // a trusted issuer is added, edited or deleted) re-fetches the open sheet, so its issuer values stay current.
    effect(() => {
      const services = this.services();
      const wanted = this.initialServiceId();
      untracked(() => {
        const current = this.selectedId();
        if (current && services.some(s => s.id === current)) {
          this.select(current, true);
          return;
        }
        const first = services.find(s => s.id === wanted) ?? services[0];
        if (first) this.select(first.id);
        else this.select('');
      });
    });
  }

  ngOnDestroy(): void {
    this.pending?.unsubscribe();
  }

  /** @param refresh Re-fetch the open sheet, keeping it on screen until the new one arrives. */
  select(id: string, refresh = false): void {
    this.pending?.unsubscribe();
    this.selectedId.set(id);
    if (!refresh) this.sheet.set(null);
    this.errorMessage.set(null);
    if (!id) return;
    this.loading.set(true);
    this.pending = this.api.getSetupSheet(id).subscribe({
      next: sheet => {
        this.loading.set(false);
        this.sheet.set(sheet);
      },
      error: (err: HttpErrorResponse) => {
        this.loading.set(false);
        this.errorMessage.set(err.message || 'Could not load the setup sheet.');
      },
    });
  }

  download(): void {
    const sheet = this.sheet();
    if (!sheet) return;
    try {
      const blob = new Blob([this.json()], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = `epic-setup-${sheet.serviceId}.json`;
      anchor.click();
      URL.revokeObjectURL(url);
    } catch {
      this.toast.error('Could not download the setup sheet.');
    }
  }
}
