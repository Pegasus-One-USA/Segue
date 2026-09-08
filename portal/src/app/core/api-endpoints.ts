/**
 * Single source of truth for backend REST endpoint paths.
 * If the API's routes change, update the path here — not in the services that call them.
 */
import { environment } from '../../environments/environment';

export const API_V1_BASE = `${environment.apiBase}/api/v1`;

// The scheme+host+port the FHIRBridge API actually answers on — what an EHR needs registered as the
// redirect/launch URI. environment.apiBase is empty in production (the portal is served same-origin by
// FHIRBridge.Gateway, see environment.prod.ts), so window.location.origin is the correct fallback there;
// in dev, apiBase already carries the API's own separate host:port (e.g. http://localhost:5000), which is
// NOT the same as the portal's own origin (e.g. http://localhost:4200).
export const APP_ORIGIN = environment.apiBase || (typeof window !== 'undefined' ? window.location.origin : '');

/** Default values for the Epic app-registration fields an admin would otherwise have to type in by hand —
 *  always resolved from the actual deployment host, never a hardcoded placeholder domain. */
export const OAUTH_DEFAULT_URLS = {
  redirectUri: `${APP_ORIGIN}/api/v1/oauth/callback`,
  launchUrl:   `${APP_ORIGIN}/api/v1/oauth/launch`,
};

// ─── Auth (AuthController — api/v1/auth) ────────────────────────────────────────
export const AUTH_ENDPOINTS = {
  login:          `${API_V1_BASE}/auth/internal/login`,
  loginMfa:       `${API_V1_BASE}/auth/internal/login/mfa`,
  logout:         `${API_V1_BASE}/auth/logout`,
  me:             `${API_V1_BASE}/auth/me`,
  refresh:        `${API_V1_BASE}/auth/refresh`,
  forgotPassword: `${API_V1_BASE}/auth/internal/forgot-password`,
  resetPassword:  `${API_V1_BASE}/auth/internal/reset-password`,
  changePassword: `${API_V1_BASE}/auth/internal/change-password`,
  magicLinkRequest: `${API_V1_BASE}/auth/magic-link/request`,
  magicLinkRedeem:  `${API_V1_BASE}/auth/magic-link/redeem`,
  // Plain full-page redirect (no XHR) — the browser is sent straight to the IdP, not through HttpClient.
  samlLogin: `${API_V1_BASE}/auth/saml/login`,
};

// ─── Tenants (TenantsController — api/v1/tenants) ───────────────────────────────
// SuperAdmin-only. The real backend for the Tenant Management screens (tenant-list, tenant-dialog,
// tenant-tab), previously backed only by an in-memory, non-persistent mock (TenantRoleService).
export const TENANT_ENDPOINTS = {
  list:   `${API_V1_BASE}/tenants`,
  paged:  `${API_V1_BASE}/tenants/paged`,
  byId:   (id: string) => `${API_V1_BASE}/tenants/${id}`,
  create: `${API_V1_BASE}/tenants`,
  update: (id: string) => `${API_V1_BASE}/tenants/${id}`,
  delete: (id: string) => `${API_V1_BASE}/tenants/${id}`,
};

// ─── Branding (BrandingController — api/v1/branding) ────────────────────────────
// GET is anonymous (login page needs it before any session exists); PUT requires configuration.write.
export const BRANDING_ENDPOINTS = {
  get: `${API_V1_BASE}/branding`,
  update: `${API_V1_BASE}/branding`,
};

// ─── MFA (MfaController — api/v1/auth/mfa) ──────────────────────────────────────
export const MFA_ENDPOINTS = {
  status:  `${API_V1_BASE}/auth/mfa/status`,
  enroll:  `${API_V1_BASE}/auth/mfa/enroll`,
  verify:  `${API_V1_BASE}/auth/mfa/verify`,
  disable: `${API_V1_BASE}/auth/mfa/disable`,
};

// ─── Users (UsersController — api/v1/users) ─────────────────────────────────────
export const USERS_ENDPOINTS = {
  list:         `${API_V1_BASE}/users`,
  byId:         (id: string) => `${API_V1_BASE}/users/${id}`,
  invite:       `${API_V1_BASE}/users/invite`,
  resendInvite: (id: string) => `${API_V1_BASE}/users/${id}/resend-invite`,
  status:       (id: string) => `${API_V1_BASE}/users/${id}/status`,
  roles:        (id: string) => `${API_V1_BASE}/users/${id}/roles`,
  removeRole:   (id: string, roleId: string) => `${API_V1_BASE}/users/${id}/roles/${roleId}`,
  permissionAllocations:       (id: string) => `${API_V1_BASE}/users/${id}/permission-allocations`,
  permissionAllocationById:    (id: string, permissionId: string) =>
    `${API_V1_BASE}/users/${id}/permission-allocations/${permissionId}`,
  mfaDisable:   (id: string) => `${API_V1_BASE}/users/${id}/mfa/disable`,
  mfaRequire:   (id: string) => `${API_V1_BASE}/users/${id}/mfa/require`,
};

// ─── Roles & Permissions (RolesController — api/v1/roles, permissions) ─────────
export const ROLES_ENDPOINTS = {
  list:  `${API_V1_BASE}/roles`,
  paged: `${API_V1_BASE}/roles/paged`,
  byId:  (id: string) => `${API_V1_BASE}/roles/${id}`,
};

export const PERMISSIONS_ENDPOINTS = {
  list:    `${API_V1_BASE}/permissions`,
  catalog: `${API_V1_BASE}/permissions/catalog`,
};

// ─── EHR Endpoints (EhrEndpointsController — api/v1/ehr-endpoints) ─────────────
export const EHR_ENDPOINTS_ENDPOINTS = {
  list: `${API_V1_BASE}/ehr-endpoints`,
  paged: `${API_V1_BASE}/ehr-endpoints/paged`,
  byId: (id: string) => `${API_V1_BASE}/ehr-endpoints/${id}`,
};

// ─── Source Connections (ConfigurationsController — api/v1/source-connections) ─
export const SOURCE_CONNECTIONS_ENDPOINTS = {
  list: `${API_V1_BASE}/source-connections`,
  paged: `${API_V1_BASE}/source-connections/paged`,
  byId: (id: string) => `${API_V1_BASE}/source-connections/${id}`,
  // WorkflowEndpoints, not ConfigurationsController — the usage check has to walk every workflow's Source
  // nodes, which only the Runtime workflow store can answer.
  usage: `${API_V1_BASE}/workflows/source-connection-usage`,
  // SourceConnectionsController — not scoped to an existing connection id, since the wizard calls this before
  // a connection is saved.
  generateSigningKey: `${API_V1_BASE}/source-connections/generate-signing-key`,
  importSigningKey: `${API_V1_BASE}/source-connections/import-signing-key`,
  // PEM downloads for a SAVED connection's signing key (SourceConnectionsController) — the public half for an EHR
  // app registration that takes an uploaded key instead of a JWKS URL, the private half (separately permissioned
  // and audit-logged server-side) for keeping a copy of a key FHIRBridge generated.
  publicKeyPem: (id: string) => `${API_V1_BASE}/source-connections/${id}/signing-key/public.pem`,
  privateKeyPem: (id: string) => `${API_V1_BASE}/source-connections/${id}/signing-key/private.pem`,
};

// ─── Notification Settings (NotificationSettingsController — api/v1/notification-settings) ─
export const NOTIFICATION_SETTINGS_ENDPOINTS = {
  get:      `${API_V1_BASE}/notification-settings`,
  update:   `${API_V1_BASE}/notification-settings`,
  testSend: `${API_V1_BASE}/notification-settings/test-send`,
};

// ─── Legal content (static files served from Content/legal, see Program.cs) ────
// Not under /api/v1 — plain static HTML, deploy-replaceable without a rebuild.
export const LEGAL_ENDPOINTS = {
  termsAndConditions: `${APP_ORIGIN}/legal/terms-and-conditions.html`,
};

// ─── Destinations (DestinationSchemaController — api/v1/destinations) ───────────
export const DESTINATION_ENDPOINTS = {
  list:                `${API_V1_BASE}/destinations`,
  paged:               `${API_V1_BASE}/destinations/paged`,
  byId:                (id: string) => `${API_V1_BASE}/destinations/${id}`,
  hasExecutionHistory: (id: string) => `${API_V1_BASE}/destinations/${id}/has-execution-history`,
  schemaPreview:       `${API_V1_BASE}/destinations/schema-preview`,
  schema:              (id: string) => `${API_V1_BASE}/destinations/${id}/schema`,
  sftpTest:            `${API_V1_BASE}/destinations/sftp-test`,
  fhirTest:            `${API_V1_BASE}/destinations/fhir-test`,
  medplumTest:         `${API_V1_BASE}/destinations/medplum-test`,
  mongoTest:           `${API_V1_BASE}/destinations/mongo-test`,
  blobTest:            `${API_V1_BASE}/destinations/blob-test`,
  // WorkflowEndpoints, not ConfigurationsController — same reasoning as SOURCE_CONNECTIONS_ENDPOINTS.usage: the
  // usage check has to walk every workflow's Destination nodes, which only the Runtime workflow store can answer.
  usage:               `${API_V1_BASE}/workflows/destination-usage`,
  addColumn:     `${API_V1_BASE}/destinations/schema/add-column`,
  createTable:   `${API_V1_BASE}/destinations/schema/create-table`,
  dropColumn:    `${API_V1_BASE}/destinations/schema/drop-column`,
  alterColumn:   `${API_V1_BASE}/destinations/schema/alter-column`,
};

// ─── De-identification profiles (DeIdentificationProfilesController — api/v1/deidentification/profiles) ──
export const DEIDENTIFICATION_ENDPOINTS = {
  list:    `${API_V1_BASE}/deidentification/profiles`,
  create:  `${API_V1_BASE}/deidentification/profiles`,
  preview: (profileId: string) => `${API_V1_BASE}/deidentification/profiles/${profileId}/preview`,
};

// ─── Mapping Profiles (ConfigurationsController / ConfigurationCatalogController — api/v1/mapping-profiles) ──
export const MAPPING_PROFILE_ENDPOINTS = {
  list:      `${API_V1_BASE}/mapping-profiles`,
  paged:     `${API_V1_BASE}/mapping-profiles/paged`,
  byId:      (id: string) => `${API_V1_BASE}/mapping-profiles/${id}`,
  activate:   (id: string) => `${API_V1_BASE}/mapping-profiles/${id}/activate`,
  deactivate: (id: string) => `${API_V1_BASE}/mapping-profiles/${id}/deactivate`,
  promoteToMaster: (id: string) => `${API_V1_BASE}/mapping-profiles/${id}/promote-to-master`,
  // WorkflowEndpoints, not ConfigurationsController — same reasoning as DESTINATION_ENDPOINTS.usage: the
  // usage check has to walk every workflow's node config (plus route-level references), which only the
  // Runtime workflow store + IConfigurationRepository together can answer.
  usage:     `${API_V1_BASE}/workflows/mapping-profile-usage`,
};

// ─── FHIR mapping catalog (MappingController — api/v1/mapping) ─────────────────
// Array-aware FHIR element metadata (correct JSONPaths, cardinality, array ancestors) generated from
// the Firely R4 model. Drives the destination wizard's field picker so paths aren't hand-guessed.
export const MAPPING_ENDPOINTS = {
  // vendor narrows the response to VendorResourceTypeSupport's known-supported list for that source
  // system (Athenahealth, Healow today) — omitted (or a vendor with no known restriction, e.g. Epic)
  // returns the full generic catalog's resource types, same as before this param existed.
  resources: (vendor?: string | null) => {
    const base = `${API_V1_BASE}/mapping/catalog/resources`;
    return vendor ? `${base}?vendor=${encodeURIComponent(vendor)}` : base;
  },
  // sourceConnectionId lets the backend resolve that source's vendor (Epic, ...) and prefer its
  // vendor-specific catalog over the generic base-FHIR-R4 one. sourceVendor is the fallback for a
  // source node that hasn't been saved yet (no real connection id assigned) but already has a vendor
  // picked in its own form — without it, a brand-new Epic source shows the generic catalog until the
  // first save round-trip. Both omitted (or falsy) always gets generic.
  resourceFields: (resourceType: string, sourceConnectionId?: string | null, sourceVendor?: string | null) => {
    const base = `${API_V1_BASE}/mapping/catalog/resources/${encodeURIComponent(resourceType)}/fields`;
    const params = new URLSearchParams();
    if (sourceConnectionId) params.set('sourceConnectionId', sourceConnectionId);
    if (sourceVendor) params.set('sourceVendor', sourceVendor);
    const qs = params.toString();
    return qs ? `${base}?${qs}` : base;
  },
};

// ─── Mapping profiles (ConfigurationsController — api/v1/mapping-profiles) ─────
// import: accepts the canonical Mapping JSON (field-mapping-summary.model.ts) wholesale and creates one
// MappingProfile per mapped resource — called when the destination wizard's "Add to Pipeline" step finishes.
export const MAPPING_PROFILES_ENDPOINTS = {
  import: `${API_V1_BASE}/mapping-profiles/import`,
};

// ─── Transformation Rules (TransformationRulesController — api/v1/transformation-rules) ───
// The 5-level scope chain (Global/DestinationType/ResourceType/Field/Workflow) that decides which of the 20
// field-level transform nodes applies to a mapped column — backs the destination wizard's "Rules" button.
export const TRANSFORMATION_RULES_ENDPOINTS = {
  list:    `${API_V1_BASE}/transformation-rules`,
  save:    `${API_V1_BASE}/transformation-rules`,
  delete:  (id: string) => `${API_V1_BASE}/transformation-rules/${id}`,
  preview: `${API_V1_BASE}/transformation-rules/preview`,
  attachPending: (workflowId: string) => `${API_V1_BASE}/transformation-rules/attach-pending/${workflowId}`,
  nodeSchemas: `${API_V1_BASE}/transformation-rules/node-schemas`,
  hidden: `${API_V1_BASE}/transformation-rules/hidden`,
  effective: `${API_V1_BASE}/transformation-rules/effective`,
  impact: `${API_V1_BASE}/transformation-rules/impact`,
};

// ─── Allowed CORS origins (AllowedCorsOriginsController — api/v1/system/allowed-origins) ──
// SuperAdmin-only: widens which browser origins the API's Portal CORS policy allows.
export const CORS_ORIGINS_ENDPOINTS = {
  list: `${API_V1_BASE}/system/allowed-origins`,
  byId: (id: string) => `${API_V1_BASE}/system/allowed-origins/${id}`,
};

// ─── System Settings (SystemSettingsController — api/v1/system/settings) ───────
// SuperAdmin-only: runtime-editable config values that override their appsettings.json default
// (e.g. worker cadence, rate limits, MFA issuer) without a redeploy. Keyed by the same dotted
// section name as the appsettings key it overrides.
export const SYSTEM_SETTINGS_ENDPOINTS = {
  list: `${API_V1_BASE}/system/settings`,
  byKey: (key: string) => `${API_V1_BASE}/system/settings/${encodeURIComponent(key)}`,
  batch: `${API_V1_BASE}/system/settings/batch`,
  decryptProvisionedSecret: `${API_V1_BASE}/system/settings/decrypt-provisioned-secret`,
};

// ─── SSO Configurations (SsoConfigurationsController — api/v1/system/sso-configurations) ───
// SuperAdmin-only: SAML + magic-link fields, DB-backed (SystemSetting rows under the hood) so a save
// here takes effect immediately, no appsettings edit or restart needed.
export const SSO_CONFIGURATIONS_ENDPOINTS = {
  get:    `${API_V1_BASE}/system/sso-configurations`,
  update: `${API_V1_BASE}/system/sso-configurations`,
};

// ─── App-level signing secrets (AppSecretsController — api/v1/system/app-secrets) ──
// SuperAdmin-only: JWT signing key / download-link signing secret, auto-generated on first boot —
// this surface only exposes metadata + on-demand regeneration, never the value itself.
export const APP_SECRETS_ENDPOINTS = {
  list: `${API_V1_BASE}/system/app-secrets`,
  regenerate: (secretName: string) => `${API_V1_BASE}/system/app-secrets/${secretName}/regenerate`,
};

export const LOINC_ENDPOINTS = {
  configuration: `${API_V1_BASE}/terminology/loinc/configuration`,
  synchronize: `${API_V1_BASE}/terminology/loinc/configuration/synchronize`,
  history: `${API_V1_BASE}/terminology/loinc/configuration/history`,
};

// ─── SNOMED CT (SnomedConfigurationController — api/v1/terminology/snomed/configuration) ──
// Auto-syncs via the shared UTS API key (see RXNORM_ENDPOINTS) on top of the original upload-driven import,
// which stays available as a fallback.
export const SNOMED_ENDPOINTS = {
  configuration: `${API_V1_BASE}/terminology/snomed/configuration`,
  synchronize: `${API_V1_BASE}/terminology/snomed/configuration/synchronize`,
  import: `${API_V1_BASE}/terminology/snomed/configuration/import`,
  history: `${API_V1_BASE}/terminology/snomed/configuration/history`,
};

// ─── ICD-10-CM (Icd10ConfigurationController — api/v1/terminology/icd10/configuration) ──
// Upload-driven import (CMS "Code Descriptions in Tabular Order" release .zip), plus a lightweight
// freshness/check-for-updates scrape — CMS/NCHS publish no version-check API, so this can't be a real scheduler.
export const ICD10_ENDPOINTS = {
  import: `${API_V1_BASE}/terminology/icd10/configuration/import`,
  history: `${API_V1_BASE}/terminology/icd10/configuration/history`,
  freshness: `${API_V1_BASE}/terminology/icd10/configuration/freshness`,
  checkForUpdates: `${API_V1_BASE}/terminology/icd10/configuration/check-for-updates`,
  downloadAndImport: `${API_V1_BASE}/terminology/icd10/configuration/download-and-import`,
};

// ─── ICD-10-PCS (Icd10PcsConfigurationController — api/v1/terminology/icd10pcs/configuration) ──
// Net-new; same upload + freshness-check shape as ICD-10-CM (no version-check API exists for this either).
export const ICD10PCS_ENDPOINTS = {
  import: `${API_V1_BASE}/terminology/icd10pcs/configuration/import`,
  history: `${API_V1_BASE}/terminology/icd10pcs/configuration/history`,
  freshness: `${API_V1_BASE}/terminology/icd10pcs/configuration/freshness`,
  checkForUpdates: `${API_V1_BASE}/terminology/icd10pcs/configuration/check-for-updates`,
  downloadAndImport: `${API_V1_BASE}/terminology/icd10pcs/configuration/download-and-import`,
};

// ─── HCPCS Level II (HcpcsConfigurationController — api/v1/terminology/hcpcs/configuration) ──
// Net-new; same upload + freshness-check shape as ICD-10-CM/PCS.
export const HCPCS_ENDPOINTS = {
  import: `${API_V1_BASE}/terminology/hcpcs/configuration/import`,
  history: `${API_V1_BASE}/terminology/hcpcs/configuration/history`,
  freshness: `${API_V1_BASE}/terminology/hcpcs/configuration/freshness`,
  checkForUpdates: `${API_V1_BASE}/terminology/hcpcs/configuration/check-for-updates`,
  downloadAndImport: `${API_V1_BASE}/terminology/hcpcs/configuration/download-and-import`,
};

// ─── NDC (NdcConfigurationController — api/v1/terminology/ndc/configuration) ──
// Net-new; auto-syncs via openFDA's public API (no required credential) — same scheduler shape as RxNorm/SNOMED.
export const NDC_ENDPOINTS = {
  configuration: `${API_V1_BASE}/terminology/ndc/configuration`,
  synchronize: `${API_V1_BASE}/terminology/ndc/configuration/synchronize`,
  import: `${API_V1_BASE}/terminology/ndc/configuration/import`,
  history: `${API_V1_BASE}/terminology/ndc/configuration/history`,
};

// ─── CVX (CvxConfigurationController — api/v1/terminology/cvx/configuration) ──
// Net-new; upload-only for now — CDC's REST API needs direct verification before a scheduler is built.
export const CVX_ENDPOINTS = {
  import: `${API_V1_BASE}/terminology/cvx/configuration/import`,
  history: `${API_V1_BASE}/terminology/cvx/configuration/history`,
};

// ─── UCUM (UcumConfigurationController — api/v1/terminology/ucum/configuration) ──
// Net-new; auto-syncs via the public ucum-org/ucum GitHub repo (no credential needed).
export const UCUM_ENDPOINTS = {
  configuration: `${API_V1_BASE}/terminology/ucum/configuration`,
  synchronize: `${API_V1_BASE}/terminology/ucum/configuration/synchronize`,
  import: `${API_V1_BASE}/terminology/ucum/configuration/import`,
  history: `${API_V1_BASE}/terminology/ucum/configuration/history`,
};

// ─── RxNorm (RxNormConfigurationController — api/v1/terminology/rxnorm/configuration) ──
// Auto-syncs monthly via NLM's UTS API (one API key, entered here, also unlocks SNOMED CT above) — the
// original upload-driven import stays available as a fallback.
export const RXNORM_ENDPOINTS = {
  configuration: `${API_V1_BASE}/terminology/rxnorm/configuration`,
  synchronize: `${API_V1_BASE}/terminology/rxnorm/configuration/synchronize`,
  import: `${API_V1_BASE}/terminology/rxnorm/configuration/import`,
  history: `${API_V1_BASE}/terminology/rxnorm/configuration/history`,
};

// ─── HAPI terminology server sync (HapiTerminologyConfigurationController — api/v1/terminology/hapi)
// One row per code system (Cvx/Dcm/Hcpcs/Icd10/Icd10Pcs/Icd11/Icpc3/Loinc/Mesh/Ndc/RxNorm/Snomed/Ucum),
// grouped settings + manual Run Now + history, shown on Settings → System Settings → General.
const HAPI_TERMINOLOGY_BASE = `${API_V1_BASE}/terminology/hapi`;
export const HAPI_TERMINOLOGY_ENDPOINTS = {
  list: HAPI_TERMINOLOGY_BASE,
  configuration: (code: string) => `${HAPI_TERMINOLOGY_BASE}/${code}`,
  runNow: (code: string) => `${HAPI_TERMINOLOGY_BASE}/${code}/run-now`,
  history: (code: string) => `${HAPI_TERMINOLOGY_BASE}/${code}/history`,
  codes: (code: string) => `${HAPI_TERMINOLOGY_BASE}/${code}/codes`,
  code: (code: string, pid: number) => `${HAPI_TERMINOLOGY_BASE}/${code}/codes/${pid}`,
  scan: (code: string) => `${HAPI_TERMINOLOGY_BASE}/${code}/scan`,
  scanAll: `${HAPI_TERMINOLOGY_BASE}/scan`,
};

// ─── Source discovery (SourceDiscoveryController — api/v1/source-discovery) ────
export const SOURCE_DISCOVERY_ENDPOINTS = {
  probe: `${API_V1_BASE}/source-discovery/probe`,
  backendAuthScopes: `${API_V1_BASE}/source-discovery/backend-auth-scopes`,
};

// ─── Source capabilities (SourceCapabilitiesController — api/v1/source-connections/{id}/…) ──
// Connection-scoped, hence the id in the path rather than a query parameter.
export const SOURCE_CAPABILITIES_ENDPOINTS = {
  /** Server-side scope generation for a SAVED connection: application type + selected resources + vendor
   *  profile, validated against the source's live scopes_supported. The authoritative answer to "what will the
   *  pipeline actually request", so Test Connection asks for exactly that instead of a wildcard of its own. */
  derivedConfig: (id: string) => `${API_V1_BASE}/source-connections/${id}/derived-config`,
};

// ─── Execution History (WorkflowEndpoints — api/v1/workflow-runs) ──────────────
// Backs the Runtime Plane's execution history (the path "Run" and interactive EHR/standalone launches actually
// take). See PIPELINE_RUNS_ENDPOINTS below for the Configured Pipeline plane's parallel history.
export const EXECUTION_HISTORY_ENDPOINTS = {
  list:      `${API_V1_BASE}/workflow-runs`,
  byId:      (id: string) => `${API_V1_BASE}/workflow-runs/${id}/summary`,
  resources: (id: string) => `${API_V1_BASE}/workflow-runs/${id}/resources`,
  nodeRuns:  (id: string) => `${API_V1_BASE}/workflow-runs/${id}/node-runs`,
  nodeRunPayload: (id: string, nodeRunId: string) => `${API_V1_BASE}/workflow-runs/${id}/node-runs/${nodeRunId}/payload`,
  fieldLineage: (id: string) => `${API_V1_BASE}/workflow-runs/${id}/field-lineage`,
  lineageSummary: (id: string) => `${API_V1_BASE}/workflow-runs/${id}/lineage/summary`,
  lineageResourceTree: (id: string) => `${API_V1_BASE}/workflow-runs/${id}/lineage/resource-tree`,
  statusCounts: `${API_V1_BASE}/workflow-runs/stats`,
};

// ─── Pipeline Executions (PipelineRunsController — api/v1/pipeline-runs) ───────
// The Configured Pipeline plane's route-execution history — scheduler/webhook-triggered runs against
// ResourcePipelineRoute, distinct from the Runtime DAG plane above. UnifiedAdmin-gated server-side.
export const PIPELINE_RUNS_ENDPOINTS = {
  routeExecutions:         `${API_V1_BASE}/pipeline-runs/route-executions`,
  routeExecutionById:      (id: string) => `${API_V1_BASE}/pipeline-runs/route-executions/${id}`,
  routeExecutionResources: (id: string) => `${API_V1_BASE}/pipeline-runs/route-executions/${id}/resources`,
  // Keyed by PipelineRunId (the batch a route execution belongs to, see PipelineExecutionEntry.pipelineRunId) —
  // one Configured Pipeline run can process several routes in one pass, so cancelling stops the whole batch,
  // not just the one route row the user clicked from.
  cancel:                  (pipelineRunId: string) => `${API_V1_BASE}/pipeline-runs/${pipelineRunId}/cancel`,
};

// ─── Governance (GovernanceController — api/v1/governance) ─────────────────────
// Read-only: audit trail, authentication log, patient/resource data-access log, security events.
// Every log type shares the same `correlationId` query param, letting the portal jump from one
// execution's CorrelationId straight to everything else that happened during it.
export const GOVERNANCE_ENDPOINTS = {
  auditLogs:          `${API_V1_BASE}/governance/audit-logs`,
  authenticationLogs: `${API_V1_BASE}/governance/authentication-logs`,
  dataAccessLogs:     `${API_V1_BASE}/governance/data-access-logs`,
  securityEvents:     `${API_V1_BASE}/governance/security-events`,
  authorizationLogs:  `${API_V1_BASE}/governance/authorization-logs`,
  hipaaAuditReport:   `${API_V1_BASE}/governance/reports/hipaa-audit`,
  soc2EvidenceReport: `${API_V1_BASE}/governance/reports/soc2-evidence`,
  smartLaunchLogs:    `${API_V1_BASE}/governance/smart-launch-logs`,
  correlationSearch:  `${API_V1_BASE}/governance/correlation-search`,
  retentionPolicies:  `${API_V1_BASE}/governance/retention-policies`,
  logSettings:        `${API_V1_BASE}/governance/log-settings`,
  archives:           `${API_V1_BASE}/governance/archives`,
  restoreArchive:     (dataClass: string) => `${API_V1_BASE}/governance/archives/${encodeURIComponent(dataClass)}/restore`,
  dataLineage:        (resourceRecordId: string) => `${API_V1_BASE}/governance/data-lineage/${resourceRecordId}`,
  revealLineageField: (resourceRecordId: string, targetField: string) =>
    `${API_V1_BASE}/governance/data-lineage/${resourceRecordId}/fields/${encodeURIComponent(targetField)}/reveal`,
  alertRules:         `${API_V1_BASE}/governance/alert-rules`,
  alertRuleById:      (id: string) => `${API_V1_BASE}/governance/alert-rules/${id}`,
  setAlertRuleEnabled: (id: string, isEnabled: boolean) => `${API_V1_BASE}/governance/alert-rules/${id}/enabled?isEnabled=${isEnabled}`,
  alerts:             `${API_V1_BASE}/governance/alerts`,
  acknowledgeAlert:   (id: string) => `${API_V1_BASE}/governance/alerts/${id}/acknowledge`,
};

// ─── Operations (OperationsController — api/v1/operations) ─────────────────────
// Read-only: scheduler dispatch history, retry history, error logs, outbound API request logs.
export const OPERATIONS_ENDPOINTS = {
  schedulerHistory: `${API_V1_BASE}/operations/scheduler-history`,
  retryHistory:     `${API_V1_BASE}/operations/retry-history`,
  errors:           `${API_V1_BASE}/operations/errors`,
  apiRequests:      `${API_V1_BASE}/operations/api-requests`,
  exports:          `${API_V1_BASE}/operations/exports`,
  notifications:    `${API_V1_BASE}/operations/notifications`,
  validationFailures: `${API_V1_BASE}/operations/validation-failures`,
  endpointHealth:     `${API_V1_BASE}/operations/endpoint-health`,
  queueMonitor:       `${API_V1_BASE}/operations/queue-monitor`,
  apiAnalytics:       `${API_V1_BASE}/operations/api-analytics`,
  systemHealth:       `${API_V1_BASE}/operations/system-health`,
  schedulerSummary:   `${API_V1_BASE}/operations/scheduler-summary`,
};

// ─── Workflows (minimal APIs — api/v1/workflows, workflow-catalog) ─────────────
export const WORKFLOW_ENDPOINTS = {
  catalog:         `${API_V1_BASE}/workflow-catalog`,
  validate:        `${API_V1_BASE}/workflows/validate`,
  build:           `${API_V1_BASE}/workflows/build`,
  list:            `${API_V1_BASE}/workflows`,
  summary:         `${API_V1_BASE}/workflows/summary`,
  create:          `${API_V1_BASE}/workflows`,
  byId:            (id: string) => `${API_V1_BASE}/workflows/${id}`,
  run:             (id: string) => `${API_V1_BASE}/workflows/${id}/run`,
  runs:            (id: string) => `${API_V1_BASE}/workflows/${id}/runs`,
  activate:        (id: string) => `${API_V1_BASE}/workflows/${id}/activate`,
  deactivate:      (id: string) => `${API_V1_BASE}/workflows/${id}/deactivate`,
  enablePublicLaunch:  (id: string) => `${API_V1_BASE}/workflows/${id}/enable-public-launch`,
  disablePublicLaunch: (id: string) => `${API_V1_BASE}/workflows/${id}/disable-public-launch`,
  copy:            (id: string) => `${API_V1_BASE}/workflows/${id}/copy`,
  launchUrl:       (id: string) => `${API_V1_BASE}/workflows/${id}/launch-url`,
  destinationData: (id: string) => `${API_V1_BASE}/workflows/${id}/destination-data`,
  checkpointUrl:    (workflowId: string, nodeId: string) => `${API_V1_BASE}/workflows/${workflowId}/nodes/${nodeId}/checkpoint-url`,
  checkpointResult: (workflowRunId: string) => `${API_V1_BASE}/workflows/runs/${workflowRunId}/checkpoint-result`,
  runStatus:       (runId: string) => `${API_V1_BASE}/workflow-runs/${runId}/status`,
  cancelRun:       (runId: string) => `${API_V1_BASE}/workflow-runs/${runId}/cancel`,
};
