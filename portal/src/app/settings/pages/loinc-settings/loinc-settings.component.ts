import { Component, OnInit, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { ToastService } from '../../../services/toast.service';
import { LoincSettingsService } from '../../services/loinc-settings.service';

@Component({ selector: 'app-loinc-settings', standalone: true, imports: [ReactiveFormsModule], templateUrl: './loinc-settings.component.html', styleUrl: './loinc-settings.component.scss' })
export class LoincSettingsComponent implements OnInit {
  private readonly fb = inject(FormBuilder); private readonly service = inject(LoincSettingsService); private readonly toast = inject(ToastService);
  protected readonly loading = signal(true); protected readonly saving = signal(false); protected readonly syncing = signal(false); protected readonly usernameSet = signal(false); protected readonly passwordSet = signal(false);
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
  ngOnInit(): void { this.service.get().subscribe({ next: x => { this.form.patchValue(x); this.usernameSet.set(x.hasUsernameConfigured); this.passwordSet.set(x.hasPasswordConfigured); this.loading.set(false); }, error: () => { this.loading.set(false); this.toast.error('Failed to load LOINC settings'); } }); }
  protected save(): void { if (this.form.invalid) return; this.saving.set(true); const v = this.form.getRawValue(); this.service.update({ ...v, username: v.username.trim() || null, password: v.password.trim() || null }).subscribe({ next: x => { this.usernameSet.set(x.hasUsernameConfigured); this.passwordSet.set(x.hasPasswordConfigured); this.form.controls.username.setValue(''); this.form.controls.password.setValue(''); this.form.markAsPristine(); this.saving.set(false); this.toast.success('LOINC settings saved'); }, error: () => { this.saving.set(false); this.toast.error('Failed to save LOINC settings'); } }); }
  protected synchronize(): void { if (this.form.dirty) { this.toast.error('Save settings before synchronizing'); return; } this.syncing.set(true); this.service.synchronize().subscribe({ next: x => { this.syncing.set(false); this.toast.success(x.alreadyCurrent ? `LOINC ${x.version} is already current` : `Imported ${x.importedConceptCount} LOINC terms`); }, error: () => { this.syncing.set(false); this.toast.error('LOINC synchronization failed'); } }); }
}
