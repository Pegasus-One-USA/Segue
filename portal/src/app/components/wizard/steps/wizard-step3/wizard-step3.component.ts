import { Component, inject, signal } from '@angular/core';
import { WizardService } from '../../../../services/wizard.service';
import { ModeConfigComponent } from '../../../shared/mode-config/mode-config.component';
import { INGESTION_MODES, EPIC_INGESTION } from '../../../../data/ingestion-modes.data';
import { ToastService } from '../../../../services/toast.service';

@Component({
  selector: 'app-wizard-step3',
  standalone: true,
  imports: [ModeConfigComponent],
  templateUrl: './wizard-step3.component.html',
  styleUrl: './wizard-step3.component.scss',
})
export class WizardStep3Component {
  protected readonly wiz   = inject(WizardService);
  private   readonly toast = inject(ToastService);

  protected readonly modes = INGESTION_MODES;

  // local mode config values (emitted from ModeConfigComponent)
  protected modeConfigValues = signal<Record<string, string>>({});

  protected allowedModes(): string[] {
    const gate = EPIC_INGESTION[this.wiz.currentApp().context];
    return gate?.allowed ?? ['search'];
  }

  protected isModeAllowed(modeId: string): boolean {
    return this.allowedModes().includes(modeId);
  }

  protected isInteractive(): boolean {
    return this.wiz.currentApp().interactive;
  }

  protected connHelpText(): string {
    return this.isInteractive()
      ? 'Interactive connection: a test authorization is performed in a browser popup. Tokens are short-lived; offline_access enables refresh.'
      : 'Backend connection: a signed JWT assertion is exchanged at the token endpoint for a short-lived access token.';
  }

  onModeChange(modeId: string): void {
    this.wiz.setMode(modeId);
  }

  onModeConfigChange(values: Record<string, string>): void {
    this.modeConfigValues.set(values);
  }

  connect(): void {
    this.wiz.markConnected();
  }

  /** Returns current mode config values for the parent to collect on save */
  getModeValues(): Record<string, string> {
    return this.modeConfigValues();
  }
}
