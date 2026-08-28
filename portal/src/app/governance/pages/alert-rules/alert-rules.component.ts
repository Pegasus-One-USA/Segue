import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatTableModule } from '@angular/material/table';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { GovernanceApiService } from '../../services/governance-api.service';
import { AlertRule, CreateAlertRuleRequest } from '../../models/governance.model';
import { HasUnsavedChanges } from '../../../core/guards/has-unsaved-changes';
import { UnsavedChangesRegistryService } from '../../../core/services/unsaved-changes-registry.service';
import { ToastService } from '../../../services/toast.service';

function emptyForm(): CreateAlertRuleRequest {
  return { name: '', eventTypeFilter: '', thresholdCount: 3, windowMinutes: 15, severity: 'Medium', recipients: '' };
}

/** The only Security Event types the backend can ever raise — see LocalAuthService,
 *  AppSecretsAdminService, and AuditChainVerificationWorker. Not user-extensible, so this is
 *  a fixed list rather than a lookup call. */
export const SECURITY_EVENT_TYPES: { value: string; label: string }[] = [
  { value: 'LoginAttemptWhileLocked', label: 'Login attempt while locked' },
  { value: 'AccountLockedThresholdReached', label: 'Account locked (threshold reached)' },
  { value: 'AppSecretRegenerated', label: 'App secret regenerated' },
  { value: 'AuditChainBroken', label: 'Audit chain broken' },
];

@Component({
  selector: 'app-alert-rules',
  standalone: true,
  imports: [CommonModule, FormsModule, MatTableModule, MatSlideToggleModule, MatTooltipModule],
  templateUrl: './alert-rules.component.html',
  styleUrl: './alert-rules.component.scss',
})
export class AlertRulesComponent implements OnInit, HasUnsavedChanges {
  private readonly api = inject(GovernanceApiService);
  private readonly unsavedChangesRegistry = inject(UnsavedChangesRegistryService);
  private readonly toast = inject(ToastService);

  constructor() {
    this.unsavedChangesRegistry.register(() => this.hasUnsavedChanges() || this.isSaveInProgress());
  }

  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly rules = signal<AlertRule[]>([]);
  readonly showForm = signal(false);
  readonly form = signal<CreateAlertRuleRequest>(emptyForm());
  readonly errorMessage = signal<string | null>(null);

  readonly displayedCols = ['name', 'eventTypeFilter', 'threshold', 'severity', 'recipients', 'isEnabled'];
  readonly eventTypes = SECURITY_EVENT_TYPES;

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.alertRules().subscribe({
      next: rules => { this.rules.set(rules); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  toggleForm(): void {
    this.showForm.set(!this.showForm());
    this.form.set(emptyForm());
  }

  updateForm<K extends keyof CreateAlertRuleRequest>(key: K, value: CreateAlertRuleRequest[K]): void {
    this.form.update(f => ({ ...f, [key]: value }));
  }

  submit(): void {
    this.errorMessage.set(null);
    this.saving.set(true);
    this.api.createAlertRule(this.form()).subscribe({
      next: () => { this.saving.set(false); this.showForm.set(false); this.load(); },
      error: () => {
        this.saving.set(false);
        this.errorMessage.set('Failed to create the alert rule. Check the fields and try again.');
      },
    });
  }

  toggleEnabled(rule: AlertRule): void {
    this.api.setAlertRuleEnabled(rule.id, !rule.isEnabled).subscribe({
      next: () => this.load(),
      error: () => this.toast.error(`Could not ${rule.isEnabled ? 'disable' : 'enable'} the alert rule.`),
    });
  }

  // ── HasUnsavedChanges (unsaved-changes.guard.ts) ────────────────────────────
  hasUnsavedChanges(): boolean {
    if (!this.showForm()) return false;
    const f = this.form();
    const empty = emptyForm();
    return (Object.keys(empty) as (keyof CreateAlertRuleRequest)[]).some(key => f[key] !== empty[key]);
  }

  isSaveInProgress(): boolean {
    return this.saving();
  }
}
