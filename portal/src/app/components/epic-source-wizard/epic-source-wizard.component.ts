import { Component, inject, signal, viewChild } from '@angular/core';
import { WizardService } from '../../services/wizard.service';
import { ToastService } from '../../services/toast.service';
import { EnvKey } from '../../models/epic-env.model';
import { EpicStepConnectComponent } from './steps/epic-step-connect/epic-step-connect.component';
import { EpicStepAuthenticateComponent } from './steps/epic-step-authenticate/epic-step-authenticate.component';
import { EpicStepDataComponent } from './steps/epic-step-data/epic-step-data.component';
import { EpicStepTestComponent } from './steps/epic-step-test/epic-step-test.component';

@Component({
  selector: 'app-epic-source-wizard',
  standalone: true,
  imports: [
    EpicStepConnectComponent,
    EpicStepAuthenticateComponent,
    EpicStepDataComponent,
    EpicStepTestComponent,
  ],
  templateUrl: './epic-source-wizard.component.html',
  styleUrl: './epic-source-wizard.component.scss',
})
export class EpicSourceWizardComponent {
  protected readonly wiz   = inject(WizardService);
  private  readonly toast  = inject(ToastService);

  protected readonly currentStep  = signal(1);
  protected readonly showAdvanced = signal(false);

  protected readonly stepConnect      = viewChild(EpicStepConnectComponent);
  protected readonly stepAuthenticate = viewChild(EpicStepAuthenticateComponent);
  protected readonly stepData         = viewChild(EpicStepDataComponent);
  protected readonly stepTest         = viewChild(EpicStepTestComponent);

  readonly STEPS = [
    { n: 1, label: 'Connect' },
    { n: 2, label: 'Authenticate' },
    { n: 3, label: 'Data' },
    { n: 4, label: 'Test' },
    { n: 5, label: 'Transform' },
    { n: 6, label: 'Destination' },
  ];

  goToStep(n: number): void {
    this.currentStep.set(Math.max(1, Math.min(6, n)));
  }

  goBack(): void {
    this.goToStep(this.currentStep() - 1);
  }

  goNext(): void {
    const step = this.currentStep();
    if (step === 1 && !this.stepConnect()?.validate()) return;
    if (step === 2 && !this.stepAuthenticate()?.validate()) return;
    if (step === 3 && !this.stepData()?.validate()) return;
    if (step === 6) { this.finish(); return; }
    this.goToStep(step + 1);
  }

  finish(): void {
    const connect = this.stepConnect();
    const auth    = this.stepAuthenticate();
    if (!connect || !auth) return;

    const cv = connect.getConnectValues();
    const av = auth.getAuthValues();

    const envKey: EnvKey = (cv.environment === 'production') ? 'production' : 'sandbox';

    this.wiz.setAppKey('provider-ehr-launch');
    this.wiz.setEnv(envKey);
    this.wiz.stepName.set(cv.appName);
    this.wiz.baseUrl.set(cv.fhirBaseUrl || cv.epicBaseUrl);
    this.wiz.token.set(cv.tokenEndpoint);
    this.wiz.authorize.set(cv.authzEndpoint);
    this.wiz.setDiscovered(connect.discoveryStatus() === 'done');

    const formValues = {
      stepName:    cv.appName,
      baseUrl:     cv.fhirBaseUrl || cv.epicBaseUrl,
      token:       cv.tokenEndpoint,
      authorize:   cv.authzEndpoint,
      algorithm:   av.signingAlgorithm,
      jwksMethod:  av.keySource === 'gen' ? 'hosted' : 'external',
      jwksUrl:     av.jwksUrl,
      kid:         av.keyId,
      kvRef:       av.keyVaultRef,
      redirectUri: cv.redirectUri,
      launchUrl:   cv.launchUrl,
    };

    this.wiz.save(formValues, {});
  }

  close(): void {
    this.wiz.close();
    this.currentStep.set(1);
  }

  toggleAdvanced(): void {
    this.showAdvanced.update(v => !v);
  }
}
