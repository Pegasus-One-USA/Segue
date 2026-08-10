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
  list: `${API_V1_BASE}/roles`,
  byId: (id: string) => `${API_V1_BASE}/roles/${id}`,
};

export const PERMISSIONS_ENDPOINTS = {
  list:    `${API_V1_BASE}/permissions`,
  catalog: `${API_V1_BASE}/permissions/catalog`,
};

// ─── EHR Endpoints (EhrEndpointsController — api/v1/ehr-endpoints) ─────────────
export const EHR_ENDPOINTS_ENDPOINTS = {
  list: `${API_V1_BASE}/ehr-endpoints`,
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
};

// ─── Notification Settings (NotificationSettingsController — api/v1/notification-settings) ─
export const NOTIFICATION_SETTINGS_ENDPOINTS = {
  get:      `${API_V1_BASE}/notification-settings`,
  update:   `${API_V1_BASE}/notification-settings`,
  testSend: `${API_V1_BASE}/notification-settings/test-send`,
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
  // WorkflowEndpoints, not ConfigurationsController — same reasoning as SOURCE_CONNECTIONS_ENDPOINTS.usage: the
  // usage check has to walk every workflow's Destination nodes, which only the Runtime workflow store can answer.
  usage:               `${API_V1_BASE}/workflows/destination-usage`,
  addColumn:     `${API_V1_BASE}/destinations/schema/add-column`,
  createTable:   `${API_V1_BASE}/destinations/schema/create-table`,
  dropColumn:    `${API_V1_BASE}/destinations/schema/drop-column`,
  alterColumn:   `${API_V1_BASE}/destinations/schema/alter-column`,
};

// ─── Mapping Profiles (ConfigurationsController / ConfigurationCatalogController — api/v1/mapping-profiles) ──
export const MAPPING_PROFILE_ENDPOINTS = {
  list:      `${API_V1_BASE}/mapping-profiles`,
  paged:     `${API_V1_BASE}/mapping-profiles/paged`,
  byId:      (id: string) => `${API_V1_BASE}/mapping-profiles/${id}`,
  activate:   (id: string) => `${API_V1_BASE}/mapping-profiles/${id}/activate`,
  deactivate: (id: string) => `${API_V1_BASE}/mapping-profiles/${id}/deactivate`,
  // WorkflowEndpoints, not ConfigurationsController — same reasoning as DESTINATION_ENDPOINTS.usage: the
  // usage check has to walk every workflow's node config (plus route-level references), which only the
  // Runtime workflow store + IConfigurationRepository together can answer.
  usage:     `${API_V1_BASE}/workflows/mapping-profile-usage`,
};

// ─── FHIR mapping catalog (MappingController — api/v1/mapping) ─────────────────
// Array-aware FHIR element metadata (correct JSONPaths, cardinality, array ancestors) generated from
// the Firely R4 model. Drives the destination wizard's field picker so paths aren't hand-guessed.
export const MAPPING_ENDPOINTS = {
  resources:     `${API_V1_BASE}/mapping/catalog/resources`,
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
  nodeSchemas: `${API_V1_BASE}/transformation-rules/node-schemas`,
  hidden: `${API_V1_BASE}/transformation-rules/hidden`,
  effective: `${API_V1_BASE}/transformation-rules/effective`,
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
  decryptProvisionedSecret: `${API_V1_BASE}/system/settings/decrypt-provisioned-secret`,
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
// Upload-driven import (RF2 Snapshot release .zip) rather than a vendor API pull — no configuration
// endpoint, just import + history.
export const SNOMED_ENDPOINTS = {
  import: `${API_V1_BASE}/terminology/snomed/configuration/import`,
  history: `${API_V1_BASE}/terminology/snomed/configuration/history`,
};

// ─── ICD-10-CM (Icd10ConfigurationController — api/v1/terminology/icd10/configuration) ──
// Upload-driven import (CMS "Code Descriptions in Tabular Order" release .zip) — same pattern as SNOMED.
export const ICD10_ENDPOINTS = {
  import: `${API_V1_BASE}/terminology/icd10/configuration/import`,
  history: `${API_V1_BASE}/terminology/icd10/configuration/history`,
};

// ─── RxNorm (RxNormConfigurationController — api/v1/terminology/rxnorm/configuration) ──
// Upload-driven import (RxNorm Full Monthly Release .zip) — same pattern as SNOMED/ICD-10.
export const RXNORM_ENDPOINTS = {
  import: `${API_V1_BASE}/terminology/rxnorm/configuration/import`,
  history: `${API_V1_BASE}/terminology/rxnorm/configuration/history`,
};

// ─── Source discovery (SourceDiscoveryController — api/v1/source-discovery) ────
export const SOURCE_DISCOVERY_ENDPOINTS = {
  probe: `${API_V1_BASE}/source-discovery/probe`,
};

// ─── Execution History (WorkflowEndpoints — api/v1/workflow-runs) ──────────────
// Backs the Runtime Plane's execution history (the path "Run" and interactive EHR/standalone launches actually
// take). See PIPELINE_RUNS_ENDPOINTS below for the Configured Pipeline plane's parallel history.
export const EXECUTION_HISTORY_ENDPOINTS = {
  list:      `${API_V1_BASE}/workflow-runs`,
  byId:      (id: string) => `${API_V1_BASE}/workflow-runs/${id}/summary`,
  resources: (id: string) => `${API_V1_BASE}/workflow-runs/${id}/resources`,
  nodeRuns:  (id: string) => `${API_V1_BASE}/workflow-runs/${id}/node-runs`,
  statusCounts: `${API_V1_BASE}/workflow-runs/stats`,
};

// ─── Pipeline Executions (PipelineRunsController — api/v1/pipeline-runs) ───────
// The Configured Pipeline plane's route-execution history — scheduler/webhook-triggered runs against
// ResourcePipelineRoute, distinct from the Runtime DAG plane above. UnifiedAdmin-gated server-side.
export const PIPELINE_RUNS_ENDPOINTS = {
  routeExecutions:         `${API_V1_BASE}/pipeline-runs/route-executions`,
  routeExecutionById:      (id: string) => `${API_V1_BASE}/pipeline-runs/route-executions/${id}`,
  routeExecutionResources: (id: string) => `${API_V1_BASE}/pipeline-runs/route-executions/${id}/resources`,
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
};
