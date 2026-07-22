import { Component, input, inject, signal, OnInit } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { WizardService } from '../../../../services/wizard.service';
import { EpicDiscoveryService } from '../../../../services/epic-discovery.service';
import { ToastService } from '../../../../services/toast.service';
import { EPIC_ENV } from '../../../../data/epic-environments.data';
import { EnvKey } from '../../../../models/epic-env.model';
import { FullDiscoveredValues, ConnectValues } from '../../models/epic-config.model';

@Component({
  selector: 'app-epic-step-connect',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './epic-step-connect.component.html',
  styleUrl: './epic-step-connect.component.scss',
})
export class EpicStepConnectComponent implements OnInit {
  readonly showAdvanced = input(false);

  protected readonly wiz      = inject(WizardService);
  private  readonly discovery = inject(EpicDiscoveryService);
  private  readonly toast     = inject(ToastService);
  private  readonly fb        = inject(FormBuilder);

  readonly discoveryStatus   = signal<'idle' | 'loading' | 'done' | 'error'>('idle');
  readonly discoveredValues  = signal<FullDiscoveredValues | null>(null);

  readonly form = this.fb.nonNullable.group({
    appName:          ['Segue Provider Launch', Validators.required],
    epicAudience:     ['provider-ehr-launch', Validators.required],
    environment:      ['sandbox', Validators.required],
    sandboxClientId:  [''],
    nonProdClientId:  [''],
    productionClientId: [''],
    organization:     ['', Validators.required],
    approvalStatus:   ['sandbox-registered'],
    launchUrl:        ['https://fhirbridge.com/launch'],
    redirectUri:      ['http://localhost:5000/api/v1/oauth/callback'],
    launchModeEnforcement: ['ehr-only'],
    enabledScopes:    [this._defaultScopes()],
    discoveryMode:    ['smart'],
    epicBaseUrl:      ['https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4'],
  });

  // Read-only display fields (not in form — populated from discovery)
  readonly fhirBaseUrl    = signal('');
  readonly fhirVersion    = signal('');
  readonly tokenEndpoint  = signal('');
  readonly authzEndpoint  = signal('');

  ngOnInit(): void {
    const name = this.wiz.stepName();
    if (name) this.form.controls.appName.setValue(name);

    const base = this.wiz.baseUrl();
    if (base) this.form.controls.epicBaseUrl.setValue(base);

    if (this.wiz.discovered()) {
      // Editing an existing node — restore discovery state
      this.discoveryStatus.set('done');
      const env = EPIC_ENV['sandbox'];
      this.discoveredValues.set({
        fhirBaseUrl:       base || env.base,
        fhirVersion:       'R4 (4.0.1)',
        tokenEndpoint:     this.wiz.token() || env.token,
        authzEndpoint:     this.wiz.authorize() || env.authorize,
        issuer:            'https://fhir.epic.com/interconnect-fhir-oauth',
        jwksUri:           'https://fhir.epic.com/interconnect-fhir-oauth/.well-known/jwks.json',
        introspectEp:      'https://fhir.epic.com/interconnect-fhir-oauth/oauth2/introspect',
        revokeEp:          'https://fhir.epic.com/interconnect-fhir-oauth/oauth2/revoke',
        signingAlgs:       'RS384, ES384',
        pkceSupport:       'S256',
        clientAuthMethods: 'client_secret_basic, private_key_jwt',
        smartCapabilities: 'launch-ehr, context-ehr-patient, sso-openid-connect, permission-user',
        supportedScopes:   'openid fhirUser launch launch/patient patient/*.read user/*.read offline_access',
      });
      this.fhirBaseUrl.set(base || env.base);
      this.fhirVersion.set('R4 (4.0.1)');
      this.tokenEndpoint.set(this.wiz.token() || env.token);
      this.authzEndpoint.set(this.wiz.authorize() || env.authorize);
    }
  }

  runDiscover(): void {
    const baseUrl = this.form.controls.epicBaseUrl.value.trim();
    if (!baseUrl) {
      this.toast.show('Epic Base URL required', 'Enter the Epic base URL before running discovery.');
      return;
    }

    const envRaw = this.form.controls.environment.value;
    const envKey: EnvKey = envRaw === 'production' ? 'production' : 'sandbox';

    this.discoveryStatus.set('loading');

    this.discovery.discover(baseUrl, envKey).subscribe({
      next: (result) => {
        const dv: FullDiscoveredValues = {
          fhirBaseUrl:       baseUrl,
          fhirVersion:       'R4 (4.0.1)',
          tokenEndpoint:     result.token,
          authzEndpoint:     result.authorize,
          issuer:            'https://fhir.epic.com/interconnect-fhir-oauth',
          jwksUri:           'https://fhir.epic.com/interconnect-fhir-oauth/.well-known/jwks.json',
          introspectEp:      result.token.replace('/token', '/introspect'),
          revokeEp:          result.token.replace('/token', '/revoke'),
          signingAlgs:       'RS384, ES384',
          pkceSupport:       result.codeChallengeMethods.length ? result.codeChallengeMethods.join(', ') : 'S256',
          clientAuthMethods: 'client_secret_basic, private_key_jwt',
          smartCapabilities: result.capabilities.length ? result.capabilities.join(', ') : '—',
          supportedScopes:   result.scopesSupported.length ? result.scopesSupported.join(' ') : '—',
        };
        this.discoveredValues.set(dv);
        this.fhirBaseUrl.set(dv.fhirBaseUrl);
        this.fhirVersion.set(dv.fhirVersion);
        this.tokenEndpoint.set(dv.tokenEndpoint);
        this.authzEndpoint.set(dv.authzEndpoint);

        this.wiz.setDiscovered(true);
        this.wiz.baseUrl.set(baseUrl);
        this.wiz.token.set(dv.tokenEndpoint);
        this.wiz.authorize.set(dv.authzEndpoint);
        // Feed the Data step: auto resource types + advertised scopes, and default the trusted issuer to this base URL.
        this.wiz.discoveredResourceTypes.set(result.resourceTypes);
        this.wiz.discoveredScopes.set(result.scopesSupported);
        if (!this.wiz.trustedIssuers().trim()) this.wiz.trustedIssuers.set(baseUrl);

        this.discoveryStatus.set('done');
        if (result.resourceTypesError) {
          this.toast.show('Discovery complete (partial)', `Endpoints resolved. Resource types unavailable: ${result.resourceTypesError}`);
        } else {
          this.toast.show('Discovery complete', `Resolved endpoints + ${result.resourceTypes.length} resource types.`);
        }
      },
      error: () => {
        this.discoveryStatus.set('error');
        this.toast.show('Discovery failed', 'Could not reach the SMART configuration endpoint.');
      },
    });
  }

  validate(): boolean {
    const v = this.form.value;
    if (!v.appName?.trim()) {
      this.toast.show('App Name required', 'Enter an application name.');
      return false;
    }
    if (!v.organization?.trim()) {
      this.toast.show('Organization required', 'Enter the customer / organization name.');
      return false;
    }
    if (this.discoveryStatus() !== 'done') {
      this.toast.show('Discover endpoints first', 'Run SMART discovery before proceeding.');
      return false;
    }
    return true;
  }

  getConnectValues(): ConnectValues {
    const v = this.form.value;
    const dv = this.discoveredValues();
    return {
      appName:           v.appName ?? 'Segue Provider Launch',
      epicAudience:      v.epicAudience ?? 'provider-ehr-launch',
      environment:       v.environment ?? 'sandbox',
      sandboxClientId:   v.sandboxClientId ?? '',
      nonProdClientId:   v.nonProdClientId ?? '',
      productionClientId: v.productionClientId ?? '',
      organization:      v.organization ?? '',
      epicBaseUrl:       v.epicBaseUrl ?? '',
      fhirBaseUrl:       dv?.fhirBaseUrl ?? v.epicBaseUrl ?? '',
      tokenEndpoint:     dv?.tokenEndpoint ?? '',
      authzEndpoint:     dv?.authzEndpoint ?? '',
      launchUrl:         v.launchUrl ?? 'https://fhirbridge.com/launch',
      redirectUri:       v.redirectUri ?? 'http://localhost:5000/api/v1/oauth/callback',
    };
  }

  get isSmartMode(): boolean {
    return this.form.controls.discoveryMode.value === 'smart';
  }

  get discoveryStatusLabel(): string {
    switch (this.discoveryStatus()) {
      case 'loading': return 'Discovering…';
      case 'done':    return 'Discovered';
      case 'error':   return 'Failed';
      default:        return 'Not run';
    }
  }

  private _defaultScopes(): string {
    return `openid fhirUser launch launch/patient
patient/Patient.read patient/Observation.read patient/Condition.read
patient/MedicationRequest.read patient/AllergyIntolerance.read
user/Patient.read user/Observation.read user/Condition.read`;
  }
}
