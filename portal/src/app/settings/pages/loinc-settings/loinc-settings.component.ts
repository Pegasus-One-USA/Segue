import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { ToastService } from '../../../services/toast.service';
import { LoincSettingsService, LoincImportHistoryEntry } from '../../services/loinc-settings.service';

const POLL_INTERVAL_MS = 3000;

@Component({ selector: 'app-loinc-settings', standalone: true, imports: [ReactiveFormsModule, DatePipe], templateUrl: './loinc-settings.component.html', styleUrl: './loinc-settings.component.scss' })
export class LoincSettingsComponent implements OnInit, OnDestroy {
  private readonly fb = inject(FormBuilder); private readonly service = inject(LoincSettingsService); private readonly toast = inject(ToastService);
  private pollTimer?: ReturnType<typeof setTimeout>;
  protected readonly loading = signal(true); protected readonly saving = signal(false); protected readonly syncing = signal(false); protected readonly usernameSet = signal(false); protected readonly passwordSet = signal(false);
  protected readonly historyLoading = signal(true); protected readonly history = signal<LoincImportHistoryEntry[]>([]);
  protected readonly form = this.fb.nonNullable.group({
    downloadApiUrl: ['https://loinc.regenstrief.org/api/v1', Validators.required],
    fhirApiUrl: ['https://fhir.loinc.org'],
    username: [''],
    password: [''],
    schedulerEnabled: [false],
    frequency: ['Monthly'],
    executionTime: ['02:00'],
    retryCount: [3, [Validators.min(0), Validators.max(10)]],
    retryIntervalSeconds: [60, [Validators.min(1), Validators.max(3600)]],
    downloadTimeoutSeconds: [900, [Validators.min(30), Validators.max(3600)]],
  });
  ngOnInit(): void {
    this.service.get().subscribe({ next: x => { this.form.patchValue(x); this.usernameSet.set(x.hasUsernameConfigured); this.passwordSet.set(x.hasPasswordConfigured); this.loading.set(false); }, error: () => { this.loading.set(false); this.toast.error('Failed to load LOINC settings'); } });
    this.loadHistory();
  }
  ngOnDestroy(): void { clearTimeout(this.pollTimer); }
  protected save(): void { if (this.form.invalid) return; this.saving.set(true); const v = this.form.getRawValue(); this.service.update({ ...v, username: v.username.trim() || null, password: v.password.trim() || null }).subscribe({ next: x => { this.usernameSet.set(x.hasUsernameConfigured); this.passwordSet.set(x.hasPasswordConfigured); this.form.controls.username.setValue(''); this.form.controls.password.setValue(''); this.form.markAsPristine(); this.saving.set(false); this.toast.success('LOINC settings saved'); }, error: () => { this.saving.set(false); this.toast.error('Failed to save LOINC settings'); } }); }
  protected synchronize(): void {
    if (this.form.dirty) { this.toast.error('Save settings before synchronizing'); return; }
    this.syncing.set(true);
    this.service.synchronize().subscribe({
      next: () => { this.syncing.set(false); this.toast.success('LOINC synchronization started in the background — check the history below for progress.'); this.loadHistory(); },
      error: () => { this.syncing.set(false); this.toast.error('LOINC synchronization failed to start'); },
    });
  }
  private loadHistory(): void {
    this.historyLoading.set(true);
    this.service.getHistory().subscribe({
      next: entries => {
        this.history.set(entries);
        this.historyLoading.set(false);
        clearTimeout(this.pollTimer);
        if (entries.some(e => e.status === 'Running')) this.pollTimer = setTimeout(() => this.loadHistory(), POLL_INTERVAL_MS);
      },
      error: () => { this.historyLoading.set(false); this.toast.error('Failed to load LOINC import history'); },
    });
  }
}
