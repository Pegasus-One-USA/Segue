import { Injectable, inject, signal, computed } from '@angular/core';
import { PipelineStore } from './pipeline.store';
import { ScopeBuilderService } from './scope-builder.service';
import { ToastService } from './toast.service';
import { EPIC_APPS } from '../data/epic-apps.data';
import { EPIC_ENV } from '../data/epic-environments.data';
import { EPIC_INGESTION } from '../data/ingestion-modes.data';
import { EPIC_MODE_CONFIG } from '../data/mode-configs.data';
import { DEFAULT_RESOURCES } from '../data/scope-constants.data';
import { AppKey, EpicApp } from '../models/epic-app.model';
import { EnvKey } from '../models/epic-env.model';
import { WizardState } from '../models/wizard-state.model';
import { SourceNode } from '../models/node.model';

export type WizardStep = 1 | 2 | 3;

export interface WizardFormValues {
  stepName:    string;
  baseUrl:     string;
  token:       string;
  authorize:   string;
  algorithm:   string;
  jwksMethod:  string;
  jwksUrl:     string;
  kid:         string;
  kvRef:       string;
  redirectUri: string;
  launchUrl:   string;
}

@Injectable({ providedIn: 'root' })
export class WizardService {
  private readonly store        = inject(PipelineStore);
  private readonly scopeBuilder = inject(ScopeBuilderService);
  private readonly toast        = inject(ToastService);

  // ── open/close ────────────────────────────────────────────────────────────
  readonly isOpen       = signal(false);
  readonly openedInline = signal(false);

  // ── step ──────────────────────────────────────────────────────────────────
  readonly step = signal<WizardStep>(1);

  // ── environment + app ─────────────────────────────────────────────────────
  readonly env    = signal<EnvKey>('sandbox');
  readonly appKey = signal<AppKey>('backend-system');

  readonly currentApp = computed<EpicApp>(() => EPIC_APPS[this.appKey()]);
  readonly currentEnv = computed(() => EPIC_ENV[this.env()]);

  readonly activeClientId = computed(() => {
    const app = this.currentApp();
    return this.env() === 'production' ? app.prodClientId : app.sandboxClientId;
  });

  // ── discovery ─────────────────────────────────────────────────────────────
  readonly discovered = signal(false);

  // ── resources + scopes ────────────────────────────────────────────────────
  readonly resources = signal<string[]>([...DEFAULT_RESOURCES]);

  // Live-discovered from the source's /metadata + smart-configuration (populated by the Connect step's Discover).
  readonly discoveredResourceTypes = signal<string[]>([]);
  readonly discoveredScopes = signal<string[]>([]);
  // Trusted issuers for EHR-launch (iss validation). Mandatory server-side for EHR launch; captured in the Data step.
  readonly trustedIssuers = signal('');

  readonly scopeString = computed(() =>
    this.scopeBuilder.buildScopes(this.currentApp(), this.resources())
  );

  // ── ingestion ─────────────────────────────────────────────────────────────
  readonly mode = signal('export');

  readonly modeValues = signal<Record<string, Record<string, string>>>({});

  // ── connection ────────────────────────────────────────────────────────────
  readonly connected = signal(false);

  // ── audience-form extra fields ────────────────────────────────────────────
  readonly clientId     = signal('');
  readonly authMethod   = signal<'secret' | 'jwt'>('secret');
  readonly epicAudience = signal('provider-ehr-launch');
  readonly redirectUri  = signal('https://fhirbridge.com/oauth/callback');
  readonly launchUrlWiz = signal('https://fhirbridge.com/launch');
  readonly isEditing    = computed(() => !!this.store.editingNodeId());

  // ── derived ingestion gate ─────────────────────────────────────────────────
  readonly allowedModes = computed(() => {
    const gate = EPIC_INGESTION[this.currentApp().context];
    return gate?.allowed ?? ['search'];
  });

  // ── form field signals (step 1) ───────────────────────────────────────────
  readonly stepName  = signal('');
  readonly baseUrl   = signal('');
  readonly token     = signal('');
  readonly authorize = signal('');

  // ── open (edit existing or create new) ────────────────────────────────────
  open(existingNodeId?: string): void {
    const node = existingNodeId ? this.store.byId(existingNodeId) : undefined;
    const f = (node?.fields ?? {}) as Record<string, string>;

    this.env.set((f['Environment'] as EnvKey) || 'sandbox');
    this.appKey.set((f['App key'] as AppKey) || 'backend-system');
    this.discovered.set(!!node);
    this.stepName.set(f['__name'] ?? 'Epic');
    this.baseUrl.set(f['FHIR base URL'] ?? EPIC_ENV[this.env()].base);
    this.token.set(f['Token endpoint'] ?? '');
    this.authorize.set(f['Authorize endpoint'] ?? '');
    this.resources.set(
      f['Resources']
        ? f['Resources'].split(',').map(s => s.trim()).filter(Boolean)
        : [...DEFAULT_RESOURCES]
    );
    const gate = EPIC_INGESTION[this.currentApp().context];
    this.mode.set(f['Ingestion mode'] || gate?.default || 'search');
    this.connected.set(!!(node as any)?.connected);
    this.modeValues.set({});

    this.clientId.set(f['Client ID'] ?? '');
    this.authMethod.set(((f['Auth method'] as any) || 'secret') as 'secret' | 'jwt');
    this.epicAudience.set(f['Epic audience'] || f['App key'] || 'provider-ehr-launch');
    this.redirectUri.set(f['Redirect URI'] ?? 'https://fhirbridge.com/oauth/callback');
    this.launchUrlWiz.set(f['Launch URL'] ?? 'https://fhirbridge.com/launch');
    this.trustedIssuers.set(f['Trusted issuers'] ?? '');

    this.store.editingNodeId.set(existingNodeId ?? null);
    this.step.set(1);
    this.isOpen.set(true);
  }

  close(): void {
    this.isOpen.set(false);
    this.openedInline.set(false);
    this.store.editingNodeId.set(null);
  }

  // ── step navigation ───────────────────────────────────────────────────────
  next(formValues: WizardFormValues, step2Valid: boolean, step3ModeValues: Record<string, string>): void {
    if (this.step() === 1) {
      if (!this.validateStep1(formValues)) return;
      this.step.set(2);
    } else if (this.step() === 2) {
      if (!step2Valid) return;
      this.step.set(3);
    } else {
      this.save(formValues, step3ModeValues);
    }
  }

  back(): void {
    if (this.step() > 1) this.step.set((this.step() - 1) as WizardStep);
  }

  // ── validation ────────────────────────────────────────────────────────────
  validateStep1(f: WizardFormValues): boolean {
    if (!f.stepName.trim()) {
      this.toast.show('Step name required', 'Enter a name for this step.');
      return false;
    }
    if (!f.baseUrl.trim()) {
      this.toast.show('FHIR base URL required', 'Enter the FHIR base URL.');
      return false;
    }
    if (!this.discovered()) {
      this.toast.show('Discover endpoints first', 'Run SMART discovery so the token/authorize endpoints are resolved.');
      return false;
    }
    return true;
  }

  validateStep2(): boolean {
    const app = this.currentApp();
    if (this.resources().length === 0) {
      this.toast.show('Pick resources', 'Select at least one resource for the scope string.');
      return false;
    }
    return true;
  }

  // ── connect ───────────────────────────────────────────────────────────────
  markConnected(): void {
    this.connected.set(true);
    const app = this.currentApp();
    this.toast.show(
      'Connection verified',
      app.interactive
        ? 'Authorization round-trip succeeded (demo).'
        : 'JWT assertion accepted by token endpoint (demo).'
    );
  }

  // ── save ──────────────────────────────────────────────────────────────────
  save(formValues: WizardFormValues, step3ModeValues: Record<string, string>): void {
    const app  = this.currentApp();
    const mode = this.mode();

    const fields: Record<string, string> = {
      '__name':          formValues.stepName.trim(),
      'Environment':     this.env(),
      'App key':         this.appKey(),
      'Registered app':  app.label,
      'App context':     app.context,
      'Active client ID': this.activeClientId(),
      'FHIR version':    'R4 (4.0.1)',
      'FHIR base URL':   formValues.baseUrl.trim(),
      'Token endpoint':  formValues.token.trim(),
      'Authorize endpoint': formValues.authorize.trim(),
      'Auth flow':       app.authFlow,
      'Resources':       this.resources().join(', '),
      'Scopes':          this.scopeString(),
      'Ingestion mode':  mode,
      ...step3ModeValues,
    };

    if (app.interactive) {
      fields['Redirect URI'] = formValues.redirectUri.trim();
      if (app.ehrLaunch) fields['Launch URL'] = formValues.launchUrl.trim();
      fields['PKCE'] = 'S256';
      // EHR launch requires ≥1 trusted issuer server-side; carry it so create-on-save (build) passes validation.
      const issuers = this.trustedIssuers().trim();
      if (issuers) fields['Trusted issuers'] = issuers;
    } else {
      fields['JWT algorithm']    = formValues.algorithm;
      fields['JWKS method']      = formValues.jwksMethod;
      if (formValues.jwksMethod === 'hosted') fields['JWKS URL'] = formValues.jwksUrl.trim();
      fields['JWT kid']          = formValues.kid.trim();
      fields['Key vault reference'] = formValues.kvRef.trim();
    }

    const editingId = this.store.editingNodeId();
    if (editingId) {
      this.store.updateNode(editingId, { fields, connected: this.connected() } as any);
      this.toast.show('Epic updated', `${fields['__name']} saved.`);
    } else {
      const count = this.store.nodes().filter(n => !n.kind).length;
      const newNode: SourceNode = {
        id:        this.store.nextNodeId(),
        kind:      undefined,
        x:         360 + count * 60,
        y:         320 + count * 40,
        fields,
        connected: this.connected(),
        abbr:      'EP',
        color:     '#ff5a4f',
      };
      this.store.addNode(newNode);
      this.toast.show('Epic added', `${fields['__name']} added to the canvas.`);
    }

    this.close();
  }

  // ── env/app change helpers ────────────────────────────────────────────────
  setEnv(env: EnvKey): void {
    this.env.set(env);
  }

  setAppKey(key: AppKey): void {
    this.appKey.set(key as AppKey);
    const gate = EPIC_INGESTION[this.currentApp().context];
    if (gate && !gate.allowed.includes(this.mode())) {
      this.mode.set(gate.default);
    }
  }

  setMode(m: string): void {
    this.mode.set(m);
  }

  toggleResource(resource: string, checked: boolean): void {
    this.resources.update(rs =>
      checked ? [...new Set([...rs, resource])] : rs.filter(r => r !== resource)
    );
  }

  setDiscovered(val: boolean): void {
    this.discovered.set(val);
  }
}
