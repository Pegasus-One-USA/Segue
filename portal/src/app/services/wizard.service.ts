import { Injectable, inject, signal, computed } from '@angular/core';
import { Subject } from 'rxjs';
import { PipelineStore } from './pipeline.store';
import { ScopeBuilderService } from './scope-builder.service';
import { ToastService } from './toast.service';
import { EPIC_APPS } from '../data/epic-apps.data';
import { EPIC_ENV } from '../data/epic-environments.data';
import { EPIC_INGESTION } from '../data/ingestion-modes.data';
import { FHIR_RESOURCES } from '../data/scope-constants.data';
import { AppKey, EpicApp } from '../models/epic-app.model';
import { EnvKey } from '../models/epic-env.model';
import { SourceNode } from '../models/node.model';
import { AUDIENCE_FIELD_CONFIG, EpicAudience } from '../components/epic-source-wizard/models/audience-field-config.data';
import { EhrVendor } from '../ehr-endpoints/models/ehr-endpoint.model';
import { ISourceConnectionService } from '../source-connections/services/i-source-connection.service';
import { SourceConnectionModel, SourceConnectionRequest, AuthenticationTypeModel } from '../source-connections/models/source-connection.model';
import { OAUTH_DEFAULT_URLS } from '../core/api-endpoints';
import { SOURCES } from '../data/sources.data';

/** EhrVendor (SourceSystemType) enum member name → SOURCES catalog id — duplicated from
 *  EHR_VENDOR_TO_SOURCE_FORM_KEY (source-form.registry.ts) rather than imported, so this service never pulls in
 *  that registry's component classes (every vendor source-form component) and risks a circular import back
 *  through EhrVendorSourceFormComponent, which already injects WizardService. */
const EHR_VENDOR_TO_SOURCES_ID: Record<string, string> = {
  Epic: 'epic',
  Cerner: 'cerner',
  Athenahealth: 'athena',
  Allscripts: 'allscripts',
  Healow: 'healow',
  MeditechGreenfield: 'meditech',
  GenericFhir: 'generic-fhir',
  Hl7v2: 'hl7v2',
  Sample: 'sample',
};

export type WizardMode = 'canvas' | 'entity';

/** Fresh (keyVaultName, secretName) pair for a wizard-typed client secret, provisioned via
 *  ConfigurationService.WriteInlineClientSecretAsync (inlineClientSecret on save) — same fixed-vault +
 *  slug-plus-random-suffix naming convention as destination-connection-secret.util.ts's newSecretName,
 *  duplicated here (not imported) since that util's own naming ("dest-...") is destination-specific. */
function newClientSecretName(connectionName: string): string {
  const slug = connectionName.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 24) || 'src';
  const suffix = Math.random().toString(36).slice(2, 8);
  return `src-${slug}-${suffix}`;
}
const CLIENT_SECRET_KEY_VAULT_NAME = 'workflow-secrets';

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
  /** The full DTO passed to openEntity() — entity mode's equivalent of editingFields() below. Named signals here
   *  only ever cover a handful of fields; long-tail data entity mode has no other way to restore (JWT key
   *  material, CDS Hooks, retrieval config, ...) reads back from this directly, the same way canvas-mode editing
   *  reads from editingFields(). Null when creating new or in canvas mode. */
  readonly entityDto    = signal<SourceConnectionModel | null>(null);
  /** Bumped after every successful entity-mode save so list pages can react via an effect() without a dialog. */
  readonly saved        = signal(0);
  /** Emits once per save() call, after the create/update HTTP call actually settles — save() itself is
   *  fire-and-forget (subscribes internally and returns immediately), so a caller that needs to know the
   *  real outcome (e.g. keep a dialog open and let the user fix a validation error, rather than assuming
   *  success the instant save() is invoked) should subscribe to this first. */
  readonly saveOutcome$ = new Subject<{ success: boolean; error?: string }>();

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
  readonly practiceId   = signal('');
  /** Existing (keyVaultName, secretName) reference for an already-saved connection's client secret — restored
   *  in openEntity() so leaving the Client Secret field blank on an edit preserves whatever secret is already
   *  stored there, instead of orphaning the reference. Null for a brand-new connection or one that has never
   *  had a secret provisioned (e.g. still using JWT/public auth). */
  readonly existingClientSecretRef = signal<{ keyVaultName: string; secretName: string } | null>(null);
  readonly authMethod   = signal<'public' | 'secret' | 'jwt'>('secret');
  readonly authPlacement = signal<'post' | 'basic'>('post');
  readonly epicAudience = signal('provider-ehr-launch');
  readonly redirectUri  = signal(OAUTH_DEFAULT_URLS.redirectUri);
  readonly launchUrlWiz = signal(OAUTH_DEFAULT_URLS.launchUrl);
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
    this.entityDto.set(null);
    // Canvas mode has no backend SourceConnection to restore a secret reference from yet (it's created later, at
    // workflow build time) — a stale reference from a previous openEntity() call must not leak in.
    this.existingClientSecretRef.set(null);

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
    this.practiceId.set(f['Practice ID'] ?? '');
    this.authMethod.set(((f['Auth method'] as string) || 'secret') as 'public' | 'secret' | 'jwt');
    this.epicAudience.set(f['Epic audience'] || f['App key'] || 'provider-ehr-launch');
    this.redirectUri.set(f['Redirect URI'] ?? OAUTH_DEFAULT_URLS.redirectUri);
    this.launchUrlWiz.set(f['Launch URL'] ?? OAUTH_DEFAULT_URLS.launchUrl);
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
    this.entityDto.set(dto);
    this.ehrType.set(dto?.sourceSystemType ?? 'Epic');

    this.env.set('sandbox');
    this.appKey.set('backend-system');
    this.discovered.set(!!dto);
    this.stepName.set(dto?.name ?? '');
    this.baseUrl.set(dto?.baseUrl ?? EPIC_ENV['sandbox'].base);
    this.token.set(dto?.authentication?.tokenEndpoint ?? '');
    this.authorize.set('');
    // Entity mode has no Resource Type & Scopes picker UI at all (removed — see ehr-vendor-source-form.component.ts's
    // showResourcePickerSection remarks), so a brand-new connection needs a real, non-empty default here
    // regardless of audience: ScopeBuilderService.buildScopes returns scopes derived ONLY from this list for a
    // non-interactive app (Backend System has no "free" base scopes the way EHR-launch/Standalone/Patient do —
    // see buildScopes), so leaving this empty silently sent scopes: [] to the backend and tripped
    // ConfigurationService's "Epic scopes are required."
    this.resources.set(
      dto?.retrieval?.resourceTypes?.length
        ? [...dto.retrieval.resourceTypes]
        : [...FHIR_RESOURCES]
    );
    const gate = EPIC_INGESTION[this.currentApp().context];
    this.setMode(gate?.default || 'search');
    this.connected.set(!!dto);
    this.modeValues.set({});

    this.clientId.set(dto?.authentication?.clientId ?? '');
    this.practiceId.set(dto?.authentication?.practiceId ?? '');
    this.existingClientSecretRef.set(
      dto?.authentication?.clientSecretKeyVaultName && dto?.authentication?.clientSecretName
        ? { keyVaultName: dto.authentication.clientSecretKeyVaultName, secretName: dto.authentication.clientSecretName }
        : null
    );
    this.authMethod.set(AUTHENTICATION_TYPE_TO_AUTH_METHOD[dto?.authentication?.authenticationType ?? 'None'] ?? 'secret');
    this.authPlacement.set((dto?.authentication?.authPlacement as 'post' | 'basic') || 'post');
    this.epicAudience.set(
      (dto?.applicationType && APPLICATION_TYPE_TO_AUDIENCE[dto.applicationType]) || 'provider-ehr-launch'
    );
    this.redirectUri.set(dto?.interactive?.redirectUris?.[0] ?? OAUTH_DEFAULT_URLS.redirectUri);
    this.launchUrlWiz.set(dto?.interactive?.launchUrl ?? OAUTH_DEFAULT_URLS.launchUrl);
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
    this.entityDto.set(null);
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
    // own `this.wiz.xxx.set(...)` calls in EhrVendorSourceFormComponent.save() just before this method is invoked).
    const audienceKey = (fields['Epic audience'] || 'provider-ehr-launch') as EpicAudience;
    const audCfg = AUDIENCE_FIELD_CONFIG[audienceKey];
    const liveAuthMethod = (fields['Auth method'] || 'secret') as 'public' | 'secret' | 'jwt';
    const liveClientId = fields['Client ID'] || null;
    // A freshly typed secret (non-blank) gets a brand-new vault reference and is provisioned via
    // inlineClientSecret; leaving it blank on an edit preserves whatever reference/secret is already stored
    // (existingClientSecretRef, restored in openEntity()) instead of orphaning it with a null/empty reference.
    const typedClientSecret = (fields['Client Secret'] ?? '').trim() || null;
    const existingSecretRef = this.existingClientSecretRef();
    const clientSecretKeyVaultName = typedClientSecret ? CLIENT_SECRET_KEY_VAULT_NAME : (existingSecretRef?.keyVaultName ?? null);
    const clientSecretName = typedClientSecret ? newClientSecretName(fields['__name'] || 'source') : (existingSecretRef?.secretName ?? null);
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
        // ScopeBuilderService.buildScopes already includes the base auth-flow scopes (openid/fhirUser/
        // launch/offline_access) for an interactive app regardless of how many resources are passed — so
        // this stays correct even with zero resources (the common case for a brand-new source; real
        // resource-derived scopes get filled in later by EpicSourceConnectionScopeSyncService once a
        // workflow wires this source to a destination — see ehr-vendor-source-form.component.ts).
        scopes:              this.scopeString().split(' ').filter(Boolean),
        clientSecretKeyVaultName: clientSecretKeyVaultName,
        clientSecretName:         clientSecretName,
        inlineClientSecret:       typedClientSecret,
        privateKeyKeyVaultName:   liveAuthMethod === 'jwt' ? (fields['Key vault reference'] || null) : null,
        privateKeySecretName:     liveAuthMethod === 'jwt' ? (fields['Secret Name'] || null) : null,
        keyId:                    liveAuthMethod === 'jwt' ? (fields['JWT kid'] || null) : null,
        // Persisted so reopening this connection (Settings → Source Connections, which has no workflow node to
        // recover it from otherwise — see EhrVendorSourceFormComponent's liveJwksUrl remarks) shows back whatever URL
        // was actually registered with the EHR, hosted or externally-typed, instead of only ever recomputing
        // FHIRBridge's own hosted URL guess.
        jwksUrl:                  liveAuthMethod === 'jwt' ? (fields['JWKS URL'] || null) : null,
        // athenahealth only — the backend builds the ah-practice reference from this bare practice id. Null for
        // every other vendor (EhrVendorSourceFormComponent only ever populates this field for Athenahealth).
        practiceId:               fields['Practice ID'] || null,
        // Where OAuth2ClientCredentialsTokenProvider places client id/secret — only meaningful for Client Secret
        // auth (liveAuthMethod === 'secret'); null (→ "post") for every other auth method, unchanged from before
        // this field existed.
        authPlacement:            liveAuthMethod === 'secret' ? ((fields['Auth placement'] as 'post' | 'basic') || 'post') : null,
      },
      interactive: audCfg.showRedirect
        ? {
            redirectUris:   fields['Redirect URI'] ? [fields['Redirect URI']] : [],
            launchUrl:       fields['Launch URL'] ?? null,
            trustedIssuers:  this.trustedIssuers().trim() ? [this.trustedIssuers().trim()] : [],
          }
        : null,
      // Retrieval (search criteria, resource types, scopes, pagination, bulk-export settings) is workflow-specific,
      // not connection-level — entity mode (Settings → Source Connections) manages only the reusable connection,
      // so it never persists a retrieval payload here regardless of what the audience would otherwise show in
      // canvas mode. See EhrVendorSourceFormComponent.showRetrievalSection, which hides the corresponding UI section.
      retrieval: (this.wizardMode() === 'canvas' && audCfg.showRetrieval)
        ? {
            retrievalMethod:        fields['Retrieval method key'] || 'search-rest',
            resourceTypes:          retrievalResourceTypes,
            searchCriteria:         fields['Search criteria'] || null,
            incrementalSyncEnabled: fields['Incremental cursor'] === 'enabled',
            // Bulk Export fields — EhrVendorSourceFormComponent.save() has always written these into `fields`
            // ('Export scope' / 'Group ID' / 'Patient ID / list' / 'FHIR output format'), but this builder never
            // read them back out, so every Bulk Export connection silently saved with a null scope/group/patient
            // list/output format regardless of what the form showed. Patient ID / list is comma-separated in the
            // form, same split-and-trim pattern as Retrieval resource type above.
            exportScope:            fields['Export scope'] || null,
            groupId:                fields['Group ID'] || null,
            patientIds:             fields['Patient ID / list']
              ? fields['Patient ID / list'].split(',').map(s => s.trim()).filter(Boolean)
              : [],
            outputFormat:           fields['FHIR output format'] || null,
          }
        : null,
    };

    // Canvas mode never calls the backend from here — it only ever adds/updates a local canvas node.
    // The real backend SourceConnection is created later, at workflow build time (WorkflowBuildAssemblerService),
    // once the whole pipeline (and the destinations that determine this source's real scopes — see
    // EpicSourceConnectionScopeSyncService) is known. This deliberately gives up the "real id exists immediately"
    // convenience destinations get from provisionDestinationConnection(), in exchange for never hitting the
    // backend's name-uniqueness check before the client-side dedup (_resolveUniqueSourceName) has had a real
    // chance to load the existing-names list — a race that eager creation here would otherwise expose on every
    // single "Add to Pipeline" click, not just an edge case.
    if (this.wizardMode() === 'canvas') {
      // Vendor-specific abbr/badge color/display name — this.ehrType() is kept in sync with whichever vendor form
      // is actually open (see EhrVendorSourceFormComponent's constructor effect), so a Cerner/MEDITECH/Allscripts/
      // etc. node gets its own SOURCES catalog entry instead of always falling back to Epic's.
      const sourceMeta = SOURCES.find((s) => s.id === (EHR_VENDOR_TO_SOURCES_ID[this.ehrType()] ?? 'epic'))
        ?? SOURCES.find((s) => s.id === 'epic')!;
      const editingId = this.store.editingNodeId();
      if (editingId) {
        // Merge onto the node's existing fields rather than replacing them outright — this form only manages a
        // subset of keys (connection/auth/retrieval); server-injected machine keys like sourceConnectionId (added
        // by create-on-save, never surfaced as a form control) must survive an edit untouched.
        const previousFields = this.store.byId(editingId)?.fields ?? {};
        this.store.updateNode(editingId, { fields: { ...previousFields, ...fields }, connected: this.connected() } as any);
        this.toast.show(`${sourceMeta.name} updated`, `${fields['__name']} saved.`);
      } else {
        const count = this.store.nodes().filter(n => !n.kind).length;
        const newNode: SourceNode = {
          id:        this.store.nextNodeId(),
          kind:      undefined,
          x:         360 + count * 60,
          y:         320 + count * 40,
          fields,
          connected: this.connected(),
          abbr:      sourceMeta.abbr,
          color:     sourceMeta.color,
          vendorId:  sourceMeta.id,
        };
        this.store.addNode(newNode);
        this.toast.show(`${sourceMeta.name} added`, `${fields['__name']} added to the canvas.`);
      }

      this.saveOutcome$.next({ success: true });
      this.close();
      return;
    }

    // ── entity mode: persist to the backend SourceConnection API ────────────
    const id = this.entityId();
    const obs = id ? this.sourceConnectionSvc.update(id, request) : this.sourceConnectionSvc.create(request);

    obs.subscribe({
      next: () => {
        this.toast.show(
          'Source Connection saved',
          id ? 'Source Connection updated successfully.' : 'Source Connection created successfully.'
        );
        this.saved.update(n => n + 1);
        this.saveOutcome$.next({ success: true });
        this.close();
      },
      error: (err) => {
        // `.error.error` first — ConfigurationService's validation failures (InvalidOperationException,
        // caught by the global handler) come back as { error: "<message>" }, not .title/.message.
        const msg = err?.error?.error ?? err?.error?.title ?? err?.error?.message ?? err?.message ?? 'Failed to save the Source Connection.';
        const errorText = typeof msg === 'string' ? msg : 'Failed to save the Source Connection.';
        this.toast.show('Save failed', errorText, 'error');
        this.saveOutcome$.next({ success: false, error: errorText });
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
