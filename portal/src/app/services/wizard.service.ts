import { Injectable, inject, signal, computed } from '@angular/core';
import { PipelineStore } from './pipeline.store';
import { ScopeBuilderService } from './scope-builder.service';
import { ToastService } from './toast.service';
import { EPIC_APPS } from '../data/epic-apps.data';
import { EPIC_ENV } from '../data/epic-environments.data';
import { EPIC_INGESTION } from '../data/ingestion-modes.data';
import { AppKey, EpicApp } from '../models/epic-app.model';
import { EnvKey } from '../models/epic-env.model';
import { SourceNode } from '../models/node.model';
import { AUDIENCE_FIELD_CONFIG, EpicAudience } from '../components/epic-source-wizard/models/audience-field-config.data';
import { EhrVendor } from '../ehr-endpoints/models/ehr-endpoint.model';
import { ISourceConnectionService } from '../source-connections/services/i-source-connection.service';
import { SourceConnectionModel, SourceConnectionRequest, AuthenticationTypeModel } from '../source-connections/models/source-connection.model';

export type WizardMode = 'canvas' | 'entity';

/** Backend ApplicationType enum member name ↔ the wizard's EpicAudience string. Null/undefined maps to
 *  'provider-ehr-launch' — ApplicationType is nullable server-side for legacy connections created before this
 *  field existed, where the grant is inferred from vendor/credentials rather than an explicit audience. */
export const APPLICATION_TYPE_TO_AUDIENCE: Record<string, string> = {
  Backend:    'backend-system',
  EhrLaunch:  'provider-ehr-launch',
  Standalone: 'provider-standalone',
  Patient:    'patient',
};
const AUDIENCE_TO_APPLICATION_TYPE: Record<string, string> = {
  'backend-system':      'Backend',
  'provider-ehr-launch': 'EhrLaunch',
  'provider-standalone': 'Standalone',
  'patient':             'Patient',
};

/** Backend AuthenticationType enum member name → the form's 3-way Client Auth Method. 'None' (no client
 *  credentials stored) must map to 'public', not 'secret' — otherwise a connection with no authentication
 *  configured at all renders as if a Client Secret were required/expected. ApiKey has no direct equivalent in
 *  this form; 'secret' is the closest fit (some stored credential value), not a precise mapping. */
export const AUTHENTICATION_TYPE_TO_AUTH_METHOD: Record<string, 'public' | 'secret' | 'jwt'> = {
  None:                  'public',
  SmartBackendServices:  'jwt',
  OAuthClientCredentials: 'secret',
  ApiKey:                'secret',
};
const AUTH_METHOD_TO_AUTHENTICATION_TYPE: Record<string, AuthenticationTypeModel> = {
  public: 'None',
  secret: 'OAuthClientCredentials',
  jwt:    'SmartBackendServices',
};

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
  /** Optional: only Backend System + JWT populates this (Key Vault secret name for the private key PEM). */
  secretName?: string;
  redirectUri: string;
  launchUrl:   string;
}

@Injectable({ providedIn: 'root' })
export class WizardService {
  private readonly store        = inject(PipelineStore);
  private readonly scopeBuilder = inject(ScopeBuilderService);
  private readonly toast        = inject(ToastService);
  private readonly sourceConnectionSvc = inject(ISourceConnectionService);

  // ── open/close ────────────────────────────────────────────────────────────
  readonly isOpen       = signal(false);
  readonly openedInline = signal(false);

  // ── canvas vs entity (CRUD page) mode ──────────────────────────────────────
  // NOTE: named `wizardMode`, not `mode` — this service already has an unrelated `mode` signal below
  // (ingestion mode: 'export'/'search'/...); reusing the name would collide with that existing signal and
  // every call site that reads/writes it (setMode(), allowedModes, wizard-step3's [mode] binding, etc.).
  /** 'canvas' (default): open()/save() read/write a Pipeline Builder canvas node via PipelineStore, exactly as
   *  before. 'entity': openEntity()/save() read/write a persisted SourceConnection via ISourceConnectionService. */
  readonly wizardMode   = signal<WizardMode>('canvas');
  /** True while viewing (not editing) a SourceConnection from the entity-mode list page. */
  readonly readonlyMode = signal(false);
  /** The vendor/EHR selector value (SourceSystemType string). Used in both modes; entity mode seeds it from the DTO. */
  readonly ehrType      = signal<EhrVendor>('Epic');
  /** The SourceConnection id being edited in entity mode; null when creating new. */
  readonly entityId     = signal<string | null>(null);
  /** Bumped after every successful entity-mode save so list pages can react via an effect() without a dialog. */
  readonly saved        = signal(0);

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
  // Empty until the user actually checks boxes (or an existing node/connection is loaded) — nothing is
  // ever preselected by default.
  readonly resources = signal<string[]>([]);

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
  readonly authMethod   = signal<'public' | 'secret' | 'jwt'>('secret');
  readonly epicAudience = signal('provider-ehr-launch');
  readonly redirectUri  = signal('http://localhost:5000/api/v1/oauth/callback');
  readonly launchUrlWiz = signal('https://fhirbridge.com/launch');
  readonly isEditing    = computed(() => !!this.store.editingNodeId() || !!this.entityId());

  /** Raw field bag of the node being edited (or null when creating new) — the source of truth for every persisted
   *  value, since named signals above only ever cover a handful of fields. Long-tail fields (retrieval config,
   *  advanced search options, JWT key material, CDS Hooks, ...) must be read back from here directly, using the
   *  exact same string keys save() writes, rather than growing this list of named signals indefinitely. */
  readonly editingFields = computed<Record<string, string> | null>(() => {
    const id = this.store.editingNodeId();
    if (!id) return null;
    return (this.store.byId(id)?.fields ?? null) as Record<string, string> | null;
  });

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

  // ── open (edit existing or create new) — canvas mode (Pipeline Builder) ───
  open(existingNodeId?: string): void {
    // Reset entity-mode state so a stale readonly/entity session from a previous openEntity() call can't leak
    // into this canvas-mode open — canvas mode is the default and must behave exactly as it always has.
    this.wizardMode.set('canvas');
    this.readonlyMode.set(false);
    this.entityId.set(null);

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
        : []
    );
    const gate = EPIC_INGESTION[this.currentApp().context];
    this.mode.set(f['Ingestion mode'] || gate?.default || 'search');
    this.connected.set(!!(node as { connected?: boolean } | null)?.connected);
    this.modeValues.set({});

    this.clientId.set(f['Client ID'] ?? '');
    this.authMethod.set(((f['Auth method'] as string) || 'secret') as 'public' | 'secret' | 'jwt');
    this.epicAudience.set(f['Epic audience'] || f['App key'] || 'provider-ehr-launch');
    this.redirectUri.set(f['Redirect URI'] ?? 'http://localhost:5000/api/v1/oauth/callback');
    this.launchUrlWiz.set(f['Launch URL'] ?? 'https://fhirbridge.com/launch');
    this.trustedIssuers.set(f['Trusted issuers'] ?? '');

    this.store.editingNodeId.set(existingNodeId ?? null);
    this.step.set(1);
    this.isOpen.set(true);
  }

  // ── open (entity mode — Source Connections CRUD page) ─────────────────────
  /** Opens the wizard against a persisted SourceConnection instead of a canvas node.
   *  Pass `dto: null` to create a new SourceConnection; pass an existing dto to view/edit it. */
  openEntity(dto: SourceConnectionModel | null, opts?: { readonly?: boolean }): void {
    this.wizardMode.set('entity');
    this.readonlyMode.set(!!opts?.readonly);
    this.entityId.set(dto?.id ?? null);
    this.ehrType.set(dto?.sourceSystemType ?? 'Epic');

    this.env.set('sandbox');
    this.appKey.set('backend-system');
    this.discovered.set(!!dto);
    this.stepName.set(dto?.name ?? '');
    this.baseUrl.set(dto?.baseUrl ?? EPIC_ENV['sandbox'].base);
    this.token.set(dto?.authentication?.tokenEndpoint ?? '');
    this.authorize.set('');
    this.resources.set(
      dto?.retrieval?.resourceTypes?.length
        ? [...dto.retrieval.resourceTypes]
        : []
    );
    const gate = EPIC_INGESTION[this.currentApp().context];
    this.setMode(gate?.default || 'search');
    this.connected.set(!!dto);
    this.modeValues.set({});

    this.clientId.set(dto?.authentication?.clientId ?? '');
    this.authMethod.set(AUTHENTICATION_TYPE_TO_AUTH_METHOD[dto?.authentication?.authenticationType ?? 'None'] ?? 'secret');
    this.epicAudience.set(
      (dto?.applicationType && APPLICATION_TYPE_TO_AUDIENCE[dto.applicationType]) || 'provider-ehr-launch'
    );
    this.redirectUri.set(dto?.interactive?.redirectUris?.[0] ?? 'http://localhost:5000/api/v1/oauth/callback');
    this.launchUrlWiz.set(dto?.interactive?.launchUrl ?? 'https://fhirbridge.com/launch');
    this.trustedIssuers.set(dto?.interactive?.trustedIssuers?.join(', ') ?? '');

    this.store.editingNodeId.set(null);
    this.step.set(1);
    this.isOpen.set(true);
  }

  close(): void {
    this.isOpen.set(false);
    this.openedInline.set(false);
    this.store.editingNodeId.set(null);
    this.wizardMode.set('canvas');
    this.readonlyMode.set(false);
    this.entityId.set(null);
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
      fields['Secret Name']     = (formValues.secretName ?? '').trim();
    }

    // Build the SourceConnectionRequest — same shape for canvas and entity mode. Canvas mode now also creates/
    // updates the real backend SourceConnection immediately (rather than deferring to workflow build), so a real
    // sourceConnectionId exists on the node right away instead of only after the whole workflow gets built.
    // Which of interactive/retrieval to send is driven by the selected audience's own field config
    // (AUDIENCE_FIELD_CONFIG), not app.interactive (a canvas-only, per-App-key concept) — Provider Standalone,
    // for example, shows BOTH a Redirect URI (interactive login) AND a Data Retrieval Method section, so it needs
    // both populated, which a single interactive-vs-retrieval binary can't express.
    // Read Client ID / Auth method from `fields` (built fresh from this exact save() call's live formValues /
    // step3ModeValues), not from `this.clientId()` / `this.authMethod()` — those WizardService signals are only
    // ever synced at open() /openEntity() time and go stale the moment the user edits the reactive form, since
    // neither field routes back through a signal write the way ehrType/resources/trustedIssuers do (see their
    // own `this.wiz.xxx.set(...)` calls in EpicAudienceFormComponent.save() just before this method is invoked).
    const audienceKey = (fields['Epic audience'] || 'provider-ehr-launch') as EpicAudience;
    const audCfg = AUDIENCE_FIELD_CONFIG[audienceKey];
    const liveAuthMethod = (fields['Auth method'] || 'secret') as 'public' | 'secret' | 'jwt';
    const liveClientId = fields['Client ID'] || null;
    const retrievalResourceTypes = fields['Retrieval resource type']
      ? fields['Retrieval resource type'].split(',').map(s => s.trim()).filter(Boolean)
      : this.resources();

    const request: SourceConnectionRequest = {
      name:             fields['__name'],
      sourceSystemType: this.ehrType(),
      baseUrl:          fields['FHIR base URL'],
      applicationType:  AUDIENCE_TO_APPLICATION_TYPE[audienceKey] ?? null,
      authentication: {
        authenticationType: AUTH_METHOD_TO_AUTHENTICATION_TYPE[liveAuthMethod] ?? 'OAuthClientCredentials',
        clientId:           liveClientId,
        tokenEndpoint:       fields['Token endpoint'] || null,
        scopes:              this.resources().length ? this.scopeString().split(' ').filter(Boolean) : [],
        clientSecretKeyVaultName: null,
        clientSecretName:         null,
        privateKeyKeyVaultName:   liveAuthMethod === 'jwt' ? (fields['Key vault reference'] || null) : null,
        privateKeySecretName:     liveAuthMethod === 'jwt' ? (fields['Secret Name'] || null) : null,
        keyId:                    liveAuthMethod === 'jwt' ? (fields['JWT kid'] || null) : null,
      },
      interactive: audCfg.showRedirect
        ? {
            redirectUris:   fields['Redirect URI'] ? [fields['Redirect URI']] : [],
            launchUrl:       fields['Launch URL'] ?? null,
            trustedIssuers:  this.trustedIssuers().trim() ? [this.trustedIssuers().trim()] : [],
          }
        : null,
      retrieval: audCfg.showRetrieval
        ? {
            retrievalMethod:        fields['Retrieval method key'] || 'search-rest',
            resourceTypes:          retrievalResourceTypes,
            searchCriteria:         fields['Search criteria'] || null,
            incrementalSyncEnabled: fields['Incremental cursor'] === 'enabled',
          }
        : null,
    };

    const isCanvas = this.wizardMode() === 'canvas';
    const editingId = isCanvas ? this.store.editingNodeId() : null;
    // Canvas mode has no entityId of its own — the real id (once provisioned) lives on the node's own fields,
    // exactly where findLaunchSourceId() and every other consumer reads it from.
    const canvasExistingSourceConnectionId = editingId ? (this.store.byId(editingId)?.fields?.['sourceConnectionId'] || null) : null;
    const id = isCanvas ? canvasExistingSourceConnectionId : this.entityId();
    const obs = id ? this.sourceConnectionSvc.update(id, request) : this.sourceConnectionSvc.create(request);

    obs.subscribe({
      next: dto => {
        if (isCanvas) {
          // Stamp the real, server-created id onto the node's fields right away — the same key
          // findLaunchSourceId()/workflow-build-assembler.service.ts already read as "already resolved".
          fields['sourceConnectionId'] = dto.id;
          if (editingId) {
            // Merge onto the node's existing fields rather than replacing them outright — this form only manages
            // a subset of keys (connection/auth/retrieval); other machine keys must survive an edit untouched.
            const previousFields = this.store.byId(editingId)?.fields ?? {};
            this.store.updateNode(editingId, { fields: { ...previousFields, ...fields }, connected: this.connected() } as any);
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
          return;
        }

        // ── entity mode: persisted to the backend SourceConnection API ──────
        this.toast.show(
          'Source Connection saved',
          id ? 'Source Connection updated successfully.' : 'Source Connection created successfully.'
        );
        this.saved.update(n => n + 1);
        this.close();
      },
      error: (err) => {
        const msg = err?.error?.title ?? err?.error?.message ?? err?.message ?? 'Failed to save the Source Connection.';
        this.toast.show('Save failed', typeof msg === 'string' ? msg : 'Failed to save the Source Connection.', 'error');
      },
    });
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

  setEhrType(v: string): void {
    this.ehrType.set(v as EhrVendor);
  }
}
