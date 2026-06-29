import { Component, inject, signal } from '@angular/core';
import { WizardService } from '../../../../services/wizard.service';
import { ToastService } from '../../../../services/toast.service';
import { CheckItem, CONFIG_CHECKS, RUNTIME_CHECKS } from '../../models/epic-config.model';

@Component({
  selector: 'app-epic-step-test',
  standalone: true,
  imports: [],
  templateUrl: './epic-step-test.component.html',
  styleUrl: './epic-step-test.component.scss',
})
export class EpicStepTestComponent {
  protected readonly wiz   = inject(WizardService);
  private  readonly toast  = inject(ToastService);

  readonly configValid  = signal(false);
  readonly testPassed   = signal(false);
  readonly validating   = signal(false);
  readonly testing      = signal(false);

  readonly configChecks  = signal<CheckItem[]>(CONFIG_CHECKS.map(c => ({ ...c, status: 'pending' as const })));
  readonly runtimeChecks = signal<CheckItem[]>(RUNTIME_CHECKS.map(c => ({ ...c, status: 'pending' as const })));

  validateConfig(): void {
    if (this.validating()) return;
    this.validating.set(true);
    this.configChecks.set(CONFIG_CHECKS.map(c => ({ ...c, status: 'pending' as const })));

    setTimeout(() => {
      this.configChecks.set(CONFIG_CHECKS.map(c => ({ ...c, status: 'ok' as const })));
      this.configValid.set(true);
      this.validating.set(false);
      this.toast.show('Configuration valid', 'All config-time checks passed.');
    }, 700);
  }

  testLaunch(): void {
    if (!this.configValid()) {
      this.toast.show('Validate first', 'Run Validate Configuration before test launch.');
      return;
    }
    if (this.testing()) return;
    this.testing.set(true);
    this.runtimeChecks.set(RUNTIME_CHECKS.map(c => ({ ...c, status: 'pending' as const })));

    setTimeout(() => {
      this.runtimeChecks.set(RUNTIME_CHECKS.map(c => ({ ...c, status: 'ok' as const })));
      this.testPassed.set(true);
      this.testing.set(false);
      this.wiz.markConnected();
    }, 1200);
  }

  checkIcon(status: string): string {
    if (status === 'ok')    return '✓';
    if (status === 'error') return '✗';
    return '○';
  }

  checkClass(status: string): string {
    if (status === 'ok')    return 'check-ok';
    if (status === 'error') return 'check-error';
    return 'check-pend';
  }
}
