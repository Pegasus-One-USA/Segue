import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { WizardServiceV2 } from '../../../../services/wizard-v2.service';
import { EpicDiscoveryService } from '../../../../services/epic-discovery.service';
import { ToastService } from '../../../../services/toast.service';
import { EnvToggleComponent } from '../../../shared/env-toggle/env-toggle.component';
import { EPIC_APPS } from '../../../../data/epic-apps-v2.data';
import { EnvKey } from '../../../../models/epic-env.model';
import { AppKey } from '../../../../models/epic-app.model';

@Component({
  selector: 'app-wizard-step1',
  standalone: true,
  imports: [FormsModule, EnvToggleComponent],
  templateUrl: './wizard-step1.component.html',
  styleUrl: './wizard-step1.component.scss',
})
export class WizardStep1Component {
  protected readonly wiz       = inject(WizardServiceV2);
  private   readonly discovery = inject(EpicDiscoveryService);
  private   readonly toast     = inject(ToastService);

  readonly discovering = signal(false);

  protected readonly apps = Object.entries(EPIC_APPS).map(([key, a]) => ({
    key,
    label: `${a.label}  —  ${a.context}`,
  }));

  // errors
  protected stepNameError = false;
  protected baseUrlError  = false;

  onEnvChange(env: EnvKey): void {
    this.wiz.setEnv(env);
    this.wiz.baseUrl.set(this.wiz.currentEnv().base);
    this.wiz.token.set('');
    this.wiz.authorize.set('');
    this.wiz.setDiscovered(false);
  }

  onAppChange(key: string): void {
    this.wiz.setAppKey(key as AppKey);
  }

  discover(): void {
    const url = this.wiz.baseUrl().trim();
    if (!url) { this.baseUrlError = true; return; }
    this.discovering.set(true);
    this.discovery.discover(url, this.wiz.env()).subscribe({
      next: ({ token, authorize }) => {
        this.wiz.token.set(token);
        this.wiz.authorize.set(authorize);
        this.wiz.setDiscovered(true);
        this.discovering.set(false);
        this.toast.show('Discovery complete', 'Token and authorize endpoints populated.');
      },
      error: () => {
        this.discovering.set(false);
        this.toast.show('Discovery failed', 'Could not reach the SMART configuration endpoint.');
      },
    });
  }

  collectAndValidate(): boolean {
    this.stepNameError = !this.wiz.stepName().trim();
    this.baseUrlError  = !this.wiz.baseUrl().trim();
    if (this.stepNameError || this.baseUrlError) return false;
    if (!this.wiz.discovered()) {
      this.toast.show('Discover endpoints first', 'Run SMART discovery to resolve the token/authorize endpoints.');
      return false;
    }
    return true;
  }
}
