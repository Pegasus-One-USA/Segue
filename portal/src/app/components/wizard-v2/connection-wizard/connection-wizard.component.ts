import { Component, inject, viewChild } from '@angular/core';
import { WizardServiceV2 } from '../../../services/wizard-v2.service';
import { ModalOverlayComponent } from '../../shared/modal-overlay/modal-overlay.component';
import { WizardRailComponent, RailStep } from '../../shared-v2/wizard-rail/wizard-rail.component';
import { WizardStep1Component } from '../steps/wizard-step1/wizard-step1.component';
import { WizardStep2Component } from '../steps/wizard-step2/wizard-step2.component';
import { WizardStep3Component } from '../steps/wizard-step3/wizard-step3.component';

const STEPS: RailStep[] = [
  { label: 'Environment &amp;&nbsp;identity' },
  { label: 'Auth, scopes &amp;&nbsp;keys' },
  { label: 'Ingestion &amp;&nbsp;connection' },
];

@Component({
  selector: 'app-connection-wizard',
  standalone: true,
  imports: [
    ModalOverlayComponent,
    WizardRailComponent,
    WizardStep1Component,
    WizardStep2Component,
    WizardStep3Component,
  ],
  templateUrl: './connection-wizard.component.html',
  styleUrl: './connection-wizard.component.scss',
})
export class ConnectionWizardComponent {
  protected readonly wiz = inject(WizardServiceV2);

  protected readonly steps = STEPS;

  // view queries for step children (to call validate/getValues)
  protected readonly step1 = viewChild(WizardStep1Component);
  protected readonly step2 = viewChild(WizardStep2Component);
  protected readonly step3 = viewChild(WizardStep3Component);

  // step2 form values cached between steps
  private _step2Cache: ReturnType<WizardStep2Component['getValues']> | null = null;

  protected progressText() {
    return `Step ${this.wiz.step()} of 3`;
  }

  protected nextLabel() {
    return this.wiz.step() === 3 ? 'Save →' : 'Next →';
  }

  next(): void {
    if (this.wiz.step() === 1) {
      const s1 = this.step1();
      if (!s1) return;
      if (!s1.collectAndValidate()) return;
      // step1 values live in wiz service signals (stepName, baseUrl, token, authorize)
      this.wiz.step.set(2);
    } else if (this.wiz.step() === 2) {
      const s2 = this.step2();
      if (!s2) return;
      if (!s2.validate()) return;
      this._step2Cache = s2.getValues();
      this.wiz.step.set(3);
    } else {
      this.save();
    }
  }

  back(): void {
    this.wiz.back();
  }

  close(): void {
    this.wiz.close();
  }

  private save(): void {
    const step2 = this._step2Cache ?? { algorithm: 'RS384', jwksMethod: 'hosted', jwksUrl: '', kid: '', kvRef: '', redirectUri: '', launchUrl: '' };
    const modeVals = this.step3()?.getModeValues() ?? {};

    const formValues = {
      stepName:    this.wiz.stepName(),
      baseUrl:     this.wiz.baseUrl(),
      token:       this.wiz.token(),
      authorize:   this.wiz.authorize(),
      algorithm:   step2.algorithm,
      jwksMethod:  step2.jwksMethod,
      jwksUrl:     step2.jwksUrl,
      kid:         step2.kid,
      kvRef:       step2.kvRef,
      redirectUri: step2.redirectUri,
      launchUrl:   step2.launchUrl,
    };

    this.wiz.save(formValues, modeVals);
  }
}
