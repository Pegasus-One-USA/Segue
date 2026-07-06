import {
  Component, output, inject, signal, computed, OnInit, DestroyRef, ElementRef, ViewChild,
} from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { ReactiveFormsModule, FormBuilder, Validators, ValidatorFn, AbstractControl, ValidationErrors } from '@angular/forms';
import { WizardService } from '../../../services/wizard.service';
import { EpicDiscoveryService } from '../../../services/epic-discovery.service';
import { ToastService } from '../../../services/toast.service';
import { EPIC_ENV } from '../../../data/epic-environments.data';
import { EnvKey } from '../../../models/epic-env.model';
import { AppKey } from '../../../models/epic-app.model';
import { FullDiscoveredValues } from '../models/epic-config.model';

export type EpicAudience = 'provider-ehr-launch' | 'provider-standalone' | 'backend-system' | 'patient';

const FHIR_RESOURCES = [
  'Patient', 'Encounter', 'Observation', 'Condition', 'MedicationRequest',
  'AllergyIntolerance', 'Immunization', 'Procedure', 'DiagnosticReport',
  'DocumentReference', 'Practitioner', 'PractitionerRole',
];

function urlValidator(ctrl: AbstractControl): ValidationErrors | null {
  if (!ctrl.value) return null;
  try { new URL(ctrl.value); return null; } catch { return { url: true }; }
}

const AUDIENCE_LABEL: Record<EpicAudience, string> = {
  'provider-ehr-launch': 'Provider EHR Launch',
  'provider-standalone': 'Provider Standalone',
  'backend-system':      'Backend System',
  'patient':             'Patient',
};

// ── Per-audience field visibility/requirement registry ─────────────────────────
// Adding a new audience means adding one entry here — no template/validator edits.
// Backend System is the only audience with zero configuration fields.
interface AudienceFieldConfig {
  showConfig: boolean;
  showLaunchUrl: boolean;
  showCdsHooks: boolean;
  /** 'readonly' = auto-populated Redirect URI (providers); 'editable' = mandatory Callback URL (patient). */
  redirectMode: 'readonly' | 'editable';
  redirectLabel: string;
  scopePrefix: 'user' | 'patient' | 'system';
}

const AUDIENCE_FIELD_CONFIG: Record<EpicAudience, AudienceFieldConfig> = {
  'provider-ehr-launch': { showConfig: true,  showLaunchUrl: true,  showCdsHooks: true,  redirectMode: 'readonly', redirectLabel: 'Redirect URI',  scopePrefix: 'user' },
  'provider-standalone': { showConfig: true,  showLaunchUrl: true,  showCdsHooks: true,  redirectMode: 'readonly', redirectLabel: 'Redirect URI',  scopePrefix: 'user' },
  'patient':             { showConfig: true,  showLaunchUrl: false, showCdsHooks: false, redirectMode: 'editable', redirectLabel: 'Callback URL',  scopePrefix: 'patient' },
  'backend-system':      { showConfig: false, showLaunchUrl: false, showCdsHooks: false, redirectMode: 'readonly', redirectLabel: '',              scopePrefix: 'system' },
};

@Component({
  selector: 'app-epic-audience-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './epic-audience-form.component.html',
  styleUrl: './epic-audience-form.component.scss',
})
export class EpicAudienceFormComponent implements OnInit {
  readonly cancelled = output<void>();
  readonly saved     = output<void>();

  @ViewChild('formRoot') private readonly formRoot?: ElementRef<HTMLElement>;

  protected readonly wiz       = inject(WizardService);
  private  readonly discovery  = inject(EpicDiscoveryService);
  private  readonly toast      = inject(ToastService);
  private  readonly fb         = inject(FormBuilder);
  private  readonly destroyRef = inject(DestroyRef);

  protected readonly resources    = FHIR_RESOURCES;
  protected readonly discStatus   = signal<'idle' | 'loading' | 'done' | 'error'>('idle');
  protected readonly discValues   = signal<FullDiscoveredValues | null>(null);
  protected readonly testStatus   = signal<'idle' | 'running' | 'ok' | 'fail'>('idle');

  protected readonly form = this.fb.nonNullable.group({
    audience:          ['provider-ehr-launch' as EpicAudience, Validators.required],
    environment:       ['sandbox', Validators.required],
    epicBaseUrl:       ['https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4', [Validators.required, urlValidator]],
    tokenEndpoint:     ['', urlValidator],
    authzEndpoint:     ['', urlValidator],
    clientId:          ['', Validators.required],
    authMethod:        ['secret'],
    clientSecret:      [''],
    jwksUrl:           ['', urlValidator],
    privateKeyRef:     [''],
    launchUrl:         ['https://fhirbridge.com/launch', urlValidator],
    callbackUrl:       ['https://fhirbridge.com/oauth/callback', [Validators.required, urlValidator]],
    resources:         [[] as string[], Validators.required],
    scopeVersion:      ['v2'],
    appName:           ['FHIRBridge Epic'],
    // ── CDS Hooks ──────────────────────────────────────────────────────────────
    cdsDiscoveryUrl:     [''],
    cdsServiceEndpoint:  [''],
    cdsTriggerHook:      [''],
    cdsReturnCard:       [''],
    cdsDtrQuestionnaire: [''],
  });

  // ── reactive bridges from RxJS FormControl.valueChanges → Signals ───────────
  // computed() only re-runs when a *signal* it reads changes; FormControl.value
  // is a plain property, so every computed below must read one of these instead
  // of `.value` directly, or it would freeze at whatever the initial value was.
  private readonly audienceValue    = toSignal(this.form.controls.audience.valueChanges,    { initialValue: this.form.controls.audience.value });
  private readonly authMethodValue  = toSignal(this.form.controls.authMethod.valueChanges,  { initialValue: this.form.controls.authMethod.value });
  private readonly environmentValue = toSignal(this.form.controls.environment.valueChanges, { initialValue: this.form.controls.environment.value });
  private readonly resourcesValue   = toSignal(this.form.controls.resources.valueChanges,   { initialValue: this.form.controls.resources.value });

  protected readonly audience   = computed(() => this.audienceValue() as EpicAudience);
  protected readonly authMethod = computed(() => this.authMethodValue());

  protected readonly audienceConfig = computed(() => AUDIENCE_FIELD_CONFIG[this.audience()]);
  protected readonly audienceLabel  = computed(() => AUDIENCE_LABEL[this.audience()]);
  protected readonly isBackend      = computed(() => !this.audienceConfig().showConfig);
  protected readonly showSecret     = computed(() => !this.isBackend() && this.authMethod() === 'secret');
  protected readonly showJwt        = computed(() => this.isBackend() || this.authMethod() === 'jwt');

  protected readonly clientIdLabel = computed(() => {
    const env = this.environmentValue();
    if (env === 'production') return 'Client ID — Production';
    if (env === 'non-production') return 'Client ID — Non-Production';
    return 'Client ID — Sandbox';
  });

  protected readonly selectedResources = computed(() => this.resourcesValue() ?? []);

  protected readonly scopeString = computed(() => {
    const aud = this.audience();
    const res = this.selectedResources();
    const pfx = this.audienceConfig().scopePrefix;
    const fixed = aud === 'backend-system'
      ? []
      : ['openid', 'fhirUser', 'offline_access', aud === 'provider-ehr-launch' ? 'launch' : 'launch/patient'];
    return [...fixed, ...res.map(r => `${pfx}/${r}.read`)].join('\n');
  });

  ngOnInit(): void {
    if (this.wiz.isEditing()) {
      // Pre-populate all fields from saved node data
      this.form.controls.audience.setValue(this.wiz.epicAudience() as EpicAudience);
      this.form.controls.environment.setValue(this.wiz.env());
      this.form.controls.clientId.setValue(this.wiz.clientId());
      this.form.controls.authMethod.setValue(this.wiz.authMethod());
      this.form.controls.callbackUrl.setValue(this.wiz.redirectUri());
      this.form.controls.launchUrl.setValue(this.wiz.launchUrlWiz());
    }

    if (this.wiz.discovered()) {
      const env = EPIC_ENV[this.wiz.env()];
      const dv: FullDiscoveredValues = {
        fhirBaseUrl: this.wiz.baseUrl() || env.base,
        fhirVersion: 'R4 (4.0.1)',
        tokenEndpoint: this.wiz.token() || env.token,
        authzEndpoint: this.wiz.authorize() || env.authorize,
        issuer: '', jwksUri: '', introspectEp: '', revokeEp: '',
        signingAlgs: 'RS384, ES384', pkceSupport: 'S256',
        clientAuthMethods: 'client_secret_basic, private_key_jwt',
        smartCapabilities: '', supportedScopes: '',
      };
      this.discValues.set(dv);
      this.discStatus.set('done');
      this.form.controls.epicBaseUrl.setValue(dv.fhirBaseUrl);
      this.form.controls.tokenEndpoint.setValue(dv.tokenEndpoint);
      this.form.controls.authzEndpoint.setValue(dv.authzEndpoint);
    }
    if (this.wiz.resources().length) {
      this.form.controls.resources.setValue(this.wiz.resources());
    }
    if (this.wiz.stepName()) {
      this.form.controls.appName.setValue(this.wiz.stepName());
    }

    this.prevAudience = this.audience();
    this.syncValidators();

    this.form.controls.audience.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((next) => {
        const nextAudience = next as EpicAudience;
        this.clearInapplicableFields(this.prevAudience, nextAudience);
        this.prevAudience = nextAudience;
        this.syncValidators();
      });

    this.form.controls.authMethod.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.syncValidators());
  }

  // ── audience field lifecycle ────────────────────────────────────────────────
  private prevAudience: EpicAudience = 'provider-ehr-launch';

  /** Clears values for fields that are no longer applicable after an audience switch. */
  private clearInapplicableFields(prev: EpicAudience, next: EpicAudience): void {
    const prevCfg = AUDIENCE_FIELD_CONFIG[prev];
    const nextCfg = AUDIENCE_FIELD_CONFIG[next];

    if (prevCfg.showConfig && !nextCfg.showConfig) {
      this.form.patchValue({
        tokenEndpoint: '', authzEndpoint: '', clientId: '', clientSecret: '',
        jwksUrl: '', privateKeyRef: '', callbackUrl: '', launchUrl: '',
        resources: [],
      });
    }

    if (prevCfg.showLaunchUrl && !nextCfg.showLaunchUrl) {
      this.form.patchValue({ launchUrl: '' });
    }

    if (prevCfg.showCdsHooks && !nextCfg.showCdsHooks) {
      this.form.patchValue({
        cdsDiscoveryUrl: '', cdsServiceEndpoint: '', cdsTriggerHook: '',
        cdsReturnCard: '', cdsDtrQuestionnaire: '',
      });
    }
  }

  /** Applies Validators.required (and URL format checks) only to fields the current audience shows. */
  private syncValidators(): void {
    const cfg    = this.audienceConfig();
    const method = this.authMethod();

    const apply = (name: string, required: boolean, isUrl = false): void => {
      const ctrl = this.form.get(name)!;
      const validators: ValidatorFn[] = isUrl ? [urlValidator] : [];
      if (required) validators.unshift(Validators.required);
      ctrl.setValidators(validators);
      ctrl.updateValueAndValidity({ emitEvent: false });
    };

    apply('epicBaseUrl',   cfg.showConfig, true);
    apply('clientId',      cfg.showConfig);
    apply('tokenEndpoint', cfg.showConfig, true);
    apply('authzEndpoint', cfg.showConfig, true);
    apply('callbackUrl',   cfg.showConfig, true);
    apply('launchUrl',     cfg.showConfig && cfg.showLaunchUrl, true);
    apply('resources',     cfg.showConfig);
    apply('clientSecret',  cfg.showConfig && method === 'secret');
    apply('jwksUrl',       cfg.showConfig && method === 'jwt', true);

    apply('cdsDiscoveryUrl',     cfg.showConfig && cfg.showCdsHooks);
    apply('cdsServiceEndpoint',  cfg.showConfig && cfg.showCdsHooks);
    apply('cdsTriggerHook',      cfg.showConfig && cfg.showCdsHooks);
    apply('cdsReturnCard',       cfg.showConfig && cfg.showCdsHooks);
    apply('cdsDtrQuestionnaire', cfg.showConfig && cfg.showCdsHooks);
  }

  protected toggleResource(r: string): void {
    const cur = this.selectedResources();
    const next = cur.includes(r) ? cur.filter(x => x !== r) : [...cur, r];
    this.form.controls.resources.setValue(next);
  }

  protected toggleAllResources(): void {
    const cur = this.selectedResources();
    this.form.controls.resources.setValue(
      cur.length === this.resources.length ? [] : [...this.resources]
    );
  }

  protected runDiscover(): void {
    const url = this.form.controls.epicBaseUrl.value.trim();
    if (!url) { this.toast.show('URL required', 'Enter Epic FHIR Base URL first.'); return; }
    const envKey: EnvKey = this.form.controls.environment.value === 'production' ? 'production' : 'sandbox';
    this.discStatus.set('loading');

    this.discovery.discover(url, envKey).subscribe({
      next: (result) => {
        const dv: FullDiscoveredValues = {
          fhirBaseUrl: url, fhirVersion: 'R4 (4.0.1)',
          tokenEndpoint: result.token, authzEndpoint: result.authorize,
          issuer: 'https://fhir.epic.com/interconnect-fhir-oauth',
          jwksUri: 'https://fhir.epic.com/interconnect-fhir-oauth/.well-known/jwks.json',
          introspectEp: result.token.replace('/token', '/introspect'),
          revokeEp: result.token.replace('/token', '/revoke'),
          signingAlgs: 'RS384, ES384', pkceSupport: 'S256',
          clientAuthMethods: 'client_secret_basic, private_key_jwt',
          smartCapabilities: 'launch-ehr, context-ehr-patient, sso-openid-connect, permission-user',
          supportedScopes: 'openid fhirUser launch launch/patient patient/*.read user/*.read offline_access',
        };
        this.discValues.set(dv);
        this.form.controls.tokenEndpoint.setValue(dv.tokenEndpoint);
        this.form.controls.authzEndpoint.setValue(dv.authzEndpoint);
        this.wiz.token.set(dv.tokenEndpoint);
        this.wiz.authorize.set(dv.authzEndpoint);
        this.wiz.setDiscovered(true);
        this.discStatus.set('done');
        this.toast.show('Discovery complete', 'SMART endpoints resolved.');
      },
      error: () => {
        this.discStatus.set('error');
        this.toast.show('Discovery failed', 'Check the URL or enter endpoints manually.');
      },
    });
  }

  protected runTestConnection(): void {
    const token = this.form.controls.tokenEndpoint.value.trim();
    if (!token) { this.toast.show('Token endpoint required', 'Run discovery or enter the token endpoint first.'); return; }
    this.testStatus.set('running');
    setTimeout(() => {
      this.testStatus.set('ok');
      this.toast.show('Test passed', 'Token endpoint reachable and responding (mock).');
    }, 2000);
  }

  protected save(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      // Wait one tick so the error classes/messages just triggered by markAllAsTouched
      // are in the DOM before we measure scroll position and focus the field.
      setTimeout(() => this.focusFirstInvalidField());
      return;
    }
    const v = this.form.value;

    const aud      = v.audience as EpicAudience;
    const envKey: EnvKey = v.environment === 'production' ? 'production' : 'sandbox';
    const appKeyMap: Record<EpicAudience, AppKey> = {
      'provider-ehr-launch': 'provider-ehr-launch',
      'provider-standalone': 'provider-standalone',
      'backend-system':      'backend-system',
      'patient':             'patient-standalone',
    };

    this.wiz.setAppKey(appKeyMap[aud]);
    this.wiz.setEnv(envKey);
    this.wiz.stepName.set(v.appName ?? 'Epic');
    this.wiz.baseUrl.set(v.epicBaseUrl ?? '');
    this.wiz.token.set(v.tokenEndpoint ?? '');
    this.wiz.authorize.set(v.authzEndpoint ?? '');
    this.wiz.resources.set(v.resources ?? []);

    this.wiz.save({
      stepName:    v.appName ?? 'Epic',
      baseUrl:     v.epicBaseUrl ?? '',
      token:       v.tokenEndpoint ?? '',
      authorize:   this.isBackend() ? '' : (v.authzEndpoint ?? ''),
      algorithm:   'RS384',
      jwksMethod:  v.authMethod === 'jwt' ? 'hosted' : 'external',
      jwksUrl:     v.jwksUrl ?? '',
      kid:         '',
      kvRef:       v.privateKeyRef ?? '',
      redirectUri: v.callbackUrl ?? '',
      launchUrl:   v.launchUrl ?? '',
    }, {
      'Client ID':             v.clientId ?? '',
      'Auth method':           v.authMethod ?? 'secret',
      'Epic audience':         aud,
      'SMART version':         'SMART App Launch 2.0 (R4)',
      'Scope version':         'v2 (granular)',
      'CDS discovery URL':     v.cdsDiscoveryUrl ?? '',
      'CDS service endpoint':  v.cdsServiceEndpoint ?? '',
      'CDS trigger hook':      v.cdsTriggerHook ?? '',
      'CDS return card':       v.cdsReturnCard ?? '',
      'DTR questionnaire':     v.cdsDtrQuestionnaire ?? '',
    });

    this.saved.emit();
  }

  protected cancel(): void { this.cancelled.emit(); }

  protected async copyRedirectUri(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.form.controls.callbackUrl.value);
      this.toast.show('Copied', 'Redirect URI copied to clipboard.');
    } catch {
      this.toast.show('Copy failed', 'Select the text manually.');
    }
  }

  /**
   * Focuses and smoothly scrolls to the first invalid field, in the order fields
   * actually appear in the (audience-dependent) rendered form — not FormGroup
   * declaration order. Works for any control bound via formControlName, plus the
   * checkbox-driven `resources` control via its [data-control] wrapper.
   */
  private focusFirstInvalidField(): void {
    const root = this.formRoot?.nativeElement;
    if (!root) return;

    const candidates = root.querySelectorAll<HTMLElement>('[formcontrolname], [data-control]');
    for (const el of Array.from(candidates)) {
      const name = el.getAttribute('formcontrolname') ?? el.getAttribute('data-control');
      const ctrl = name ? this.form.get(name) : null;
      if (!ctrl || ctrl.valid) continue;

      const focusTarget = el.matches('input, select, textarea')
        ? el
        : (el.querySelector<HTMLElement>('input, select, textarea') ?? el);

      el.scrollIntoView({ behavior: 'smooth', block: 'center' });
      focusTarget.focus({ preventScroll: true });
      return;
    }
  }
}
