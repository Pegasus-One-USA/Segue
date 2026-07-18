import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatTableModule } from '@angular/material/table';
import { GovernanceApiService } from '../../services/governance-api.service';
import { AlertRule, CreateAlertRuleRequest } from '../../models/governance.model';

function emptyForm(): CreateAlertRuleRequest {
  return { name: '', eventTypeFilter: '', thresholdCount: 3, windowMinutes: 15, severity: 'Medium', recipients: '' };
}

@Component({
  selector: 'app-alert-rules',
  standalone: true,
  imports: [CommonModule, FormsModule, MatTableModule],
  templateUrl: './alert-rules.component.html',
  styleUrl: './alert-rules.component.scss',
})
export class AlertRulesComponent implements OnInit {
  private readonly api = inject(GovernanceApiService);

  readonly loading = signal(false);
  readonly rules = signal<AlertRule[]>([]);
  readonly showForm = signal(false);
  readonly form = signal<CreateAlertRuleRequest>(emptyForm());
  readonly errorMessage = signal<string | null>(null);

  readonly displayedCols = ['name', 'eventTypeFilter', 'threshold', 'severity', 'recipients', 'isEnabled'];

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
    this.api.createAlertRule(this.form()).subscribe({
      next: () => { this.showForm.set(false); this.load(); },
      error: () => this.errorMessage.set('Failed to create the alert rule. Check the fields and try again.'),
    });
  }

  toggleEnabled(rule: AlertRule): void {
    this.api.setAlertRuleEnabled(rule.id, !rule.isEnabled).subscribe({ next: () => this.load() });
  }
}
