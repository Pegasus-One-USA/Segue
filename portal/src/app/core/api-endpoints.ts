/**
 * Single source of truth for backend REST endpoint paths.
 * If the API's routes change, update the path here — not in the services that call them.
 */
import { environment } from '../../environments/environment';

export const API_V1_BASE = `${environment.apiBase}/api/v1`;

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
  byId: (id: string) => `${API_V1_BASE}/source-connections/${id}`,
  // WorkflowEndpoints, not ConfigurationsController — the usage check has to walk every workflow's Source
  // nodes, which only the Runtime workflow store can answer.
  usage: `${API_V1_BASE}/workflows/source-connection-usage`,
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
  // WorkflowEndpoints, not ConfigurationsController — same reasoning as SOURCE_CONNECTIONS_ENDPOINTS.usage: the
  // usage check has to walk every workflow's Destination nodes, which only the Runtime workflow store can answer.
  usage:               `${API_V1_BASE}/workflows/destination-usage`,
  addColumn:     `${API_V1_BASE}/destinations/schema/add-column`,
  createTable:   `${API_V1_BASE}/destinations/schema/create-table`,
  dropColumn:    `${API_V1_BASE}/destinations/schema/drop-column`,
  alterColumn:   `${API_V1_BASE}/destinations/schema/alter-column`,
};

// ─── FHIR mapping catalog (MappingController — api/v1/mapping) ─────────────────
// Array-aware FHIR element metadata (correct JSONPaths, cardinality, array ancestors) generated from
// the Firely R4 model. Drives the destination wizard's field picker so paths aren't hand-guessed.
export const MAPPING_ENDPOINTS = {
  resources:     `${API_V1_BASE}/mapping/catalog/resources`,
  // sourceConnectionId lets the backend resolve that source's vendor (Epic, ...) and prefer its
  // vendor-specific catalog over the generic base-FHIR-R4 one — omitted (or falsy) always gets generic.
  resourceFields: (resourceType: string, sourceConnectionId?: string | null) => {
    const base = `${API_V1_BASE}/mapping/catalog/resources/${encodeURIComponent(resourceType)}/fields`;
    return sourceConnectionId ? `${base}?sourceConnectionId=${encodeURIComponent(sourceConnectionId)}` : base;
  },
};

// ─── Mapping profiles (ConfigurationsController — api/v1/mapping-profiles) ─────
// import: accepts the canonical Mapping JSON (field-mapping-summary.model.ts) wholesale and creates one
// MappingProfile per mapped resource — called when the destination wizard's "Add to Pipeline" step finishes.
export const MAPPING_PROFILES_ENDPOINTS = {
  import: `${API_V1_BASE}/mapping-profiles/import`,
};

// ─── Source discovery (SourceDiscoveryController — api/v1/source-discovery) ────
export const SOURCE_DISCOVERY_ENDPOINTS = {
  probe: `${API_V1_BASE}/source-discovery/probe`,
};

// ─── Execution History (WorkflowEndpoints — api/v1/workflow-runs) ──────────────
// Backs the Runtime Plane's execution history (the path "Run" and interactive EHR/standalone launches actually
// take). The Configured Pipeline has its own parallel route-execution history under /pipeline-runs/route-executions,
// used only by the route/schedule/webhook path — not currently surfaced in the portal since it has no UI trigger.
export const EXECUTION_HISTORY_ENDPOINTS = {
  list:      `${API_V1_BASE}/workflow-runs`,
  byId:      (id: string) => `${API_V1_BASE}/workflow-runs/${id}/summary`,
  resources: (id: string) => `${API_V1_BASE}/workflow-runs/${id}/resources`,
};

// ─── Governance: user activity (UserActivityLogsController — api/v1/user-activity-logs) ────
export const USER_ACTIVITY_LOGS_ENDPOINTS = {
  list: `${API_V1_BASE}/user-activity-logs`,
};

// ─── Governance: operational/pipeline audit logs (OperationalAuditLogsController — api/v1/audit-logs) ────
export const OPERATIONAL_LOGS_ENDPOINTS = {
  list: `${API_V1_BASE}/audit-logs/paged`,
};

// ─── Governance: resource lineage / chain of custody (LineageController — api/v1/lineage) ────
export const LINEAGE_ENDPOINTS = {
  list:   `${API_V1_BASE}/lineage/entries`,
  chain:  `${API_V1_BASE}/lineage`,
  fields: `${API_V1_BASE}/lineage/fields`,
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
};
