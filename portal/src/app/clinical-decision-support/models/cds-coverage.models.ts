// Shapes of the coverage requirements (Da Vinci CRD) admin API: payers, their coverage rules, rule file import /
// export and the payer relay test (api/v1/cds/payers, api/v1/cds/coverage-rules). Mirrors CdsCoverageDtos.cs.
// Payer configuration only, never patient data. Dates are "yyyy-MM-dd".

// ─── Codes (CdsCoverageCodes.cs) ────────────────────────────────────────────────

/** The handler key of the built-in coverage requirements check (CdsHandlerKeys.CoverageRequirements). */
export const CDS_COVERAGE_HANDLER_KEY = 'coverage-requirements';

export type CdsCovered = 'covered' | 'not-covered' | 'conditional';
/** "satisfied" is in the CRD value set, but a local rule cannot assert it (it needs a payer-issued id per patient). */
export type CdsPaNeeded = 'no-auth' | 'auth-needed' | 'satisfied' | 'performpa' | 'conditional';
export type CdsDocNeeded = 'clinical' | 'admin' | 'both' | 'conditional';
/** "OTH" is v3-NullFlavor "other", which the CRD value sets include. */
export type CdsDocPurpose = 'withpa' | 'withclaim' | 'withorder' | 'retain-doc' | 'OTH';
export type CdsInfoNeeded = 'performer' | 'location' | 'timeframe' | 'contract-window' | 'OTH';
export type CdsCodeMatch = 'exact' | 'prefix';

export const CDS_COVERED_VALUES: CdsCovered[] = ['covered', 'not-covered', 'conditional'];
/** What a local rule may assert (no "satisfied"). */
export const CDS_PA_NEEDED_FOR_RULES: CdsPaNeeded[] = ['no-auth', 'auth-needed', 'performpa', 'conditional'];
export const CDS_DOC_NEEDED_VALUES: CdsDocNeeded[] = ['clinical', 'admin', 'both', 'conditional'];
export const CDS_DOC_PURPOSE_VALUES: CdsDocPurpose[] = ['withpa', 'withclaim', 'withorder', 'retain-doc', 'OTH'];
export const CDS_INFO_NEEDED_VALUES: CdsInfoNeeded[] = ['performer', 'location', 'timeframe', 'contract-window', 'OTH'];

/** Limits the API enforces (CdsPayer.cs, CdsCoverageRule.cs). */
export const CDS_COVERAGE_LIMITS = {
  payerNameMax: 200,
  identifierMax: 200,
  maxIdentifiers: 20,
  maxPlanValues: 50,
  urlMax: 1000,
  clientIdMax: 200,
  relayTimeoutMin: 100,
  relayTimeoutMax: 10_000,
  relayTimeoutDefault: 1000,
  systemMax: 200,
  codeMax: 100,
  reasonMax: 500,
} as const;

// ─── Payers ─────────────────────────────────────────────────────────────────────

export interface CdsPayer {
  id: string;
  name: string;
  /** Matched against Coverage.payor: "value", "system|value", or the payer name as the EHR shows it. */
  identifiers: string[];
  /** Optional Coverage.class values; when not empty one must be on the Coverage. */
  planValues: string[];
  isEnabled: boolean;
  relayEnabled: boolean;
  relayServiceUrl: string | null;
  relayTimeoutMs: number;
  fallbackToLocalRules: boolean;
  /** Off by default: the EHR's access token is not passed to the payer. */
  forwardFhirAuthorization: boolean;
  /** The JWT sub Segue signs relayed calls with (the client id the payer gave Segue). */
  relayClientId: string | null;
  ruleCount: number;
  enabledRuleCount: number;
  createdOnUtc: string;
  createdBy: string | null;
  modifiedOnUtc: string | null;
  modifiedBy: string | null;
}

/** Create and update body (PUT replaces every field). */
export interface SaveCdsPayerRequest {
  name: string;
  identifiers: string[];
  planValues: string[];
  isEnabled: boolean;
  relayEnabled: boolean;
  relayServiceUrl: string | null;
  relayTimeoutMs: number;
  fallbackToLocalRules: boolean;
  forwardFhirAuthorization: boolean;
  relayClientId: string | null;
}

/** POST payers/{id}/test-relay: a synthetic, non-PHI order-sign request to the payer, signed as Segue. */
export interface CdsPayerRelayTestResult {
  payerId: string;
  relayServiceUrl: string | null;
  reachable: boolean;
  httpStatus: number | null;
  durationMs: number;
  cardCount: number;
  systemActionCount: number;
  /** "ok" or a short failure reason ("timed out", "HTTP 401", "unreachable", ...). */
  outcome: string;
  message: string;
}

// ─── Coverage rules ─────────────────────────────────────────────────────────────

export interface CdsCoverageRule {
  id: string;
  payerId: string;
  payerName: string | null;
  system: string;
  code: string;
  codeMatch: CdsCodeMatch;
  covered: CdsCovered;
  paNeeded: CdsPaNeeded | null;
  docNeeded: CdsDocNeeded | null;
  docPurposes: CdsDocPurpose[];
  infoNeeded: CdsInfoNeeded[];
  reason: string | null;
  policyUrl: string | null;
  /** DTR Questionnaire canonical (url or url|version). */
  questionnaire: string | null;
  effectiveFrom: string;
  effectiveTo: string | null;
  isEnabled: boolean;
  /** Enabled and today (UTC) is within the effective dates. */
  isEffectiveToday: boolean;
  createdOnUtc: string;
  createdBy: string | null;
  modifiedOnUtc: string | null;
  modifiedBy: string | null;
}

/** Create and update body (PUT replaces every field; the payer cannot change on update). */
export interface SaveCdsCoverageRuleRequest {
  payerId: string;
  system: string;
  code: string;
  codeMatch: CdsCodeMatch;
  covered: CdsCovered;
  paNeeded: CdsPaNeeded | null;
  docNeeded: CdsDocNeeded | null;
  docPurposes: CdsDocPurpose[];
  infoNeeded: CdsInfoNeeded[];
  reason: string | null;
  policyUrl: string | null;
  questionnaire: string | null;
  /** Null means 2000-01-01 ("always"). */
  effectiveFrom: string | null;
  effectiveTo: string | null;
  isEnabled: boolean;
}

export interface CdsCoverageRuleFilter {
  payerId?: string;
  /** Code starts with this. */
  code?: string;
  /** Text in the system, code or reason. */
  search?: string;
  enabled?: boolean;
  page: number;
  pageSize: number;
}

export interface CdsCoverageRulePage {
  items: CdsCoverageRule[];
  totalCount: number;
  page: number;
  pageSize: number;
}

// ─── Import / export ────────────────────────────────────────────────────────────

export type CdsCoverageFileFormat = 'csv' | 'json';
export type CdsCoverageImportMode = 'preview' | 'apply';
/** merge: add and update, keep the rest. replace: the file becomes the whole rule set of each payer it names. */
export type CdsCoverageImportStrategy = 'merge' | 'replace';
export type CdsCoverageImportAction = 'add' | 'update' | 'unchanged' | 'delete' | 'error';

export interface CdsCoverageImportRow {
  /** CSV: the record number (header = 1). JSON: the 1-based position. 0 for a rule a replace would delete. */
  rowNumber: number;
  payer: string | null;
  system: string | null;
  code: string | null;
  effectiveFrom: string | null;
  action: CdsCoverageImportAction;
  errors: string[];
}

export interface CdsCoverageImportResult {
  mode: CdsCoverageImportMode;
  format: CdsCoverageFileFormat;
  strategy: CdsCoverageImportStrategy;
  /** True only for an apply with no errors (the changes were saved). */
  applied: boolean;
  totalRows: number;
  validRows: number;
  added: number;
  updated: number;
  unchanged: number;
  deleted: number;
  errorCount: number;
  /** Problems with the file as a whole (unreadable, missing columns, too many rows). */
  fileErrors: string[];
  rows: CdsCoverageImportRow[];
}

export interface CdsCoverageImportRequest {
  file: File;
  format: CdsCoverageFileFormat;
  mode: CdsCoverageImportMode;
  strategy: CdsCoverageImportStrategy;
  /** Every row belongs to this payer (the file's payer column may then be blank). */
  payerId: string | null;
}

/** A downloaded file (export or template). */
export interface CdsDownloadedFile {
  fileName: string;
  blob: Blob;
}
