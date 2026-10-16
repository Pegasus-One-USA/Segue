import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { MatIconModule } from '@angular/material/icon';
import { Subscription } from 'rxjs';
import { CdsApiService } from '../../services/cds-api.service';
import { CdsRequest, CdsServiceInstance, CdsTestResult } from '../../models/cds.models';
import { cdsHookLabel } from '../../models/cds-labels';
import { CdsTestResultComponent } from '../../components/cds-test-result/cds-test-result.component';
import { ToastService } from '../../../services/toast.service';

/**
 * Test a service without an EHR: load its sample request, edit it, run it and see the cards. Runs through the
 * admin API, so no EHR token is needed, it works on services that are off, and nothing is stored or logged.
 */
@Component({
  selector: 'app-cds-test',
  standalone: true,
  imports: [RouterLink, MatIconModule, CdsTestResultComponent],
  templateUrl: './cds-test.component.html',
  styleUrl: './cds-test.component.scss',
})
export class CdsTestComponent implements OnInit {
  private readonly api   = inject(CdsApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly toast = inject(ToastService);

  readonly loading       = signal(true);
  readonly services      = signal<CdsServiceInstance[]>([]);
  readonly selectedId    = signal('');
  readonly requestText   = signal('');
  readonly loadingSample = signal(false);
  readonly running       = signal(false);
  readonly inputError    = signal<string | null>(null);
  readonly runError      = signal<string | null>(null);
  readonly result        = signal<CdsTestResult | null>(null);

  /** The sample load or run in flight. Picking another service cancels it, so its answer never lands on the new one. */
  private pending: Subscription | null = null;

  readonly selected = computed(() => this.services().find(s => s.id === this.selectedId()) ?? null);
  readonly hookLabel = cdsHookLabel;

  ngOnInit(): void {
    this.api.getServices().subscribe({
      next: services => {
        this.services.set(services);
        this.loading.set(false);
        const requested = this.route.snapshot.queryParamMap.get('service');
        if (requested && services.some(s => s.id === requested)) this.selectService(requested);
      },
      error: () => {
        this.loading.set(false);
        this.toast.error('Could not load CDS services.');
      },
    });
  }

  selectService(id: string): void {
    this.cancelPending();
    this.selectedId.set(id);
    this.result.set(null);
    this.runError.set(null);
    this.inputError.set(null);
    if (id) this.loadSample();
    else this.requestText.set('');
  }

  loadSample(): void {
    const id = this.selectedId();
    if (!id) return;
    this.cancelPending();
    this.loadingSample.set(true);
    this.inputError.set(null);
    this.pending = this.api.getSampleRequest(id).subscribe({
      next: sample => {
        this.loadingSample.set(false);
        this.requestText.set(JSON.stringify(sample, null, 2));
      },
      error: (err: HttpErrorResponse) => {
        this.loadingSample.set(false);
        this.inputError.set(err.message || 'Could not load the sample request.');
      },
    });
  }

  onRequestInput(text: string): void {
    this.requestText.set(text);
    this.inputError.set(null);
  }

  run(): void {
    const id = this.selectedId();
    if (!id) return;
    this.runError.set(null);

    const request = this.parseRequest();
    if (!request) return;

    this.cancelPending();
    this.running.set(true);
    this.result.set(null);
    this.pending = this.api.testService(id, request).subscribe({
      next: result => {
        this.running.set(false);
        this.result.set(result);
      },
      error: (err: HttpErrorResponse) => {
        this.running.set(false);
        this.runError.set(err.message || 'The test could not be run.');
      },
    });
  }

  clear(): void {
    this.requestText.set('');
    this.result.set(null);
    this.inputError.set(null);
    this.runError.set(null);
  }

  private cancelPending(): void {
    this.pending?.unsubscribe();
    this.pending = null;
    this.loadingSample.set(false);
    this.running.set(false);
  }

  private parseRequest(): CdsRequest | null {
    const text = this.requestText().trim();
    if (!text) {
      this.inputError.set('Enter a request, or load the sample.');
      return null;
    }
    let parsed: unknown;
    try {
      parsed = JSON.parse(text);
    } catch {
      this.inputError.set('This is not valid JSON. Check for a missing comma, quote or bracket.');
      return null;
    }
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      this.inputError.set('The request must be a JSON object with "hook", "hookInstance" and "context".');
      return null;
    }
    return parsed as CdsRequest;
  }
}
