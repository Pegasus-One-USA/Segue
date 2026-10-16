// Shapes of the CDS Hooks admin API (api/v1/cds/...) and of the CDS Hooks 2.0 wire format the Test screen sends
// and renders. Admin DTOs are camelCase with string enums; wire names are pinned to the spec (see
// CdsHooksWireModels.cs), which is why fhirAuthorization keeps its snake_case members.

// ─── Handler catalog ────────────────────────────────────────────────────────────

export type CdsSettingType = 'number' | 'text' | 'select' | 'boolean' | 'list';

/** A setting value as stored and returned by the API. */
export type CdsSettingValue = string | number | boolean | string[] | null;

export interface CdsSettingOption {
  value: string;
  label: string;
}

export interface CdsSettingDescriptor {
  key: string;
  label: string;
  type: CdsSettingType;
  defaultValue: CdsSettingValue;
  help: string | null;
  options: CdsSettingOption[] | null;
  required: boolean;
  min: number | null;
  max: number | null;
}

export interface CdsHandler {
  key: string;
  displayName: string;
  description: string;
  supportedHooks: string[];
  settings: CdsSettingDescriptor[];
  /** Hook → (prefetch key → FHIR query template). */
  prefetchByHook: Record<string, Record<string, string>>;
  /** The one-click fixes this check can offer (empty when none). */
  fixTypes: CdsFixTypeDescriptor[];
}

// ─── One-click fixes (CDS Hooks suggestions) ────────────────────────────────────

export type CdsFixMappingFieldKey = 'trigger' | 'system' | 'code' | 'display';

/** One column of a fix type's code-mapping table. */
export interface CdsFixMappingField {
  key: CdsFixMappingFieldKey | string;
  label: string;
  required: boolean;
  help: string | null;
}

export type CdsActionType = 'create' | 'delete';

/** One kind of fix a check can offer (one action in the EHR: create or delete one resource). */
export interface CdsFixTypeDescriptor {
  key: string;
  label: string;
  actionType: CdsActionType | string;
  resourceType: string;
  /** Empty for a fix that needs no code (removing a draft order). */
  mappingFields: CdsFixMappingField[];
  /** The fix can't be offered until at least one code mapping is saved. */
  requiresMapping: boolean;
  /** What a mapping can be keyed by (drug class, care gap...). Null: one mapping for every case. */
  triggers: CdsSettingOption[] | null;
  /** The code systems a mapping may use (first is the default). */
  codeSystems: CdsSettingOption[] | null;
  help: string | null;
}

export interface CdsFixCodeMapping {
  /** Null or absent means "any trigger". */
  trigger?: string | null;
  system: string;
  code: string;
  display?: string | null;
}

export interface CdsFixTypeSetting {
  key: string;
  enabled: boolean;
  mappings: CdsFixCodeMapping[];
}

export interface CdsFixSettings {
  /** Master switch: off means no card carries a fix whatever the per-type switches say. */
  enabled: boolean;
  fixTypes: CdsFixTypeSetting[];
  recommendedFixType?: string | null;
}

// ─── Dismiss reasons and card links ─────────────────────────────────────────────

/** A reason a clinician can pick when dismissing a card. Codes are what the health system maps in the EHR. */
export interface CdsOverrideReasonOption {
  code: string;
  display: string;
}

export type CdsLinkType = 'absolute' | 'smart';

/** A link button added to every card of a service. */
export interface CdsCardLinkOption {
  label: string;
  url: string;
  type: CdsLinkType;
  /** SMART links only. */
  appContext?: string | null;
  /** SMART links only. */
  autolaunchable?: boolean;
}

// ─── Service instances ──────────────────────────────────────────────────────────

export interface CdsServiceInstance {
  id: string;
  serviceId: string;
  title: string;
  handlerKey: string;
  /** Null when the handler type is no longer registered. */
  handlerDisplayName: string | null;
  hook: string;
  description: string;
  isEnabled: boolean;
  /** Stored settings with the handler's defaults filled in (what the rules actually use). */
  settings: Record<string, CdsSettingValue>;
  /**
   * Only the values an admin set. Send these back (never `settings`) when saving without editing the settings,
   * so keys left at their default keep following the handler's defaults.
   */
  storedSettings: Record<string, CdsSettingValue>;
  cardSourceLabel: string;
  cardSourceUrl: string | null;
  /** Empty = any enabled trusted issuer may call. */
  allowedIssuerIds: string[];
  /** The handler's prefetch templates for this hook. */
  prefetch: Record<string, string>;
  createdOnUtc: string;
  createdBy: string | null;
  modifiedOnUtc: string | null;
  modifiedBy: string | null;
  /** Fetch prefetch data the EHR left out from the EHR's own FHIR server. */
  fetchMissingData: boolean;
  /** Always an object; `{ enabled: false, fixTypes: [] }` when never set. */
  fixes: CdsFixSettings;
  /** True when cards may change the clinician's draft orders or problem list ("Changes EHR drafts"). */
  fixesEnabled: boolean;
  /** The reasons cards offer: the service's own list, else the standard ones. */
  overrideReasons: CdsOverrideReasonOption[];
  /** True when the service uses the standard reasons (no list of its own). */
  overrideReasonsAreDefault: boolean;
  links: CdsCardLinkOption[];
  /** The check's fix types (empty when the check is unavailable or offers none). */
  supportedFixTypes: CdsFixTypeDescriptor[];
}

/** Create and update body. The whole service is replaced on update, so always send every member. */
export interface SaveCdsServiceInstanceRequest {
  serviceId: string;
  title: string;
  handlerKey: string;
  hook: string;
  description: string | null;
  isEnabled: boolean;
  settings: Record<string, CdsSettingValue> | null;
  cardSourceLabel: string | null;
  cardSourceUrl: string | null;
  allowedIssuerIds: string[] | null;
  fetchMissingData: boolean;
  /** Null means fixes off. */
  fixes: CdsFixSettings | null;
  /** Null means the standard reasons. */
  overrideReasons: CdsOverrideReasonOption[] | null;
  links: CdsCardLinkOption[] | null;
}

// ─── Trusted issuers ────────────────────────────────────────────────────────────

export interface CdsTrustedIssuer {
  id: string;
  name: string;
  vendor: string;
  issuer: string;
  jwksUrl: string | null;
  jwksJson: string | null;
  audience: string | null;
  isEnabled: boolean;
  createdOnUtc: string;
  createdBy: string | null;
  modifiedOnUtc: string | null;
  modifiedBy: string | null;
  /** The token `sub` to require (Epic: the CDS client id). Null: not checked. */
  expectedSubject: string | null;
  /** The FHIR server Segue may fetch missing data from. Null: the issuer address is used. */
  fhirBaseUrl: string | null;
  /** fhirBaseUrl, else the issuer. */
  effectiveFhirBaseUrl: string;
  /** Let Segue fetch missing data from this EHR. */
  allowCallback: boolean;
}

export interface SaveCdsTrustedIssuerRequest {
  name: string;
  vendor: string;
  issuer: string;
  jwksUrl: string | null;
  jwksJson: string | null;
  audience: string | null;
  isEnabled: boolean;
  expectedSubject: string | null;
  fhirBaseUrl: string | null;
  allowCallback: boolean;
}

/** POST trusted-issuers/{id}/test-callback: GET {FHIR base}/metadata, sent with no token and no patient data. */
export interface CdsCallbackTestResult {
  trustedIssuerId: string;
  fhirBaseUrl: string;
  allowCallback: boolean;
  reachable: boolean;
  httpStatus: number | null;
  fhirVersion: string | null;
  software: string | null;
  durationMs: number;
  message: string;
}

// ─── Epic setup sheet ───────────────────────────────────────────────────────────

export interface CdsSetupSheetPrefetch {
  key: string;
  query: string;
  /** Segue fetches it itself when the EHR leaves it out. */
  fetchedIfMissing: boolean;
}

export interface CdsSetupSheetFix {
  fixTypeKey: string;
  label: string;
  actionType: string;
  resourceType: string;
  trigger: string | null;
  system: string | null;
  code: string | null;
  display: string | null;
  isRecommended: boolean;
  /** The trigger's label (e.g. "Penicillins"); null for "any". */
  triggerLabel?: string | null;
}

export interface CdsSetupSheetIssuer {
  id: string;
  name: string;
  vendor: string;
  issuer: string;
  jwksUrl: string | null;
  hasPastedJwks: boolean;
  expectedSubject: string | null;
  expectedAudience: string;
  effectiveFhirBaseUrl: string;
  allowCallback: boolean;
  isEnabled: boolean;
}

/** Every value the health system's Epic team enters by hand for one service (Epic does not read discovery). */
export interface CdsSetupSheet {
  serviceInstanceId: string;
  serviceId: string;
  title: string;
  hook: string;
  isEnabled: boolean;
  serviceUrl: string;
  feedbackUrl: string;
  defaultAudience: string;
  publicBaseUrlConfigured: boolean;
  fetchMissingData: boolean;
  fixesEnabled: boolean;
  prefetch: CdsSetupSheetPrefetch[];
  overrideReasons: CdsOverrideReasonOption[];
  overrideReasonSystem: string;
  fixes: CdsSetupSheetFix[];
  links: CdsCardLinkOption[];
  issuers: CdsSetupSheetIssuer[];
  warnings: string[];
  generatedOnUtc: string;
}

// ─── Discovery info ─────────────────────────────────────────────────────────────

export interface CdsDiscoveryInfoService {
  id: string;
  serviceId: string;
  title: string;
  hook: string;
  isEnabled: boolean;
  /** Also the default JWT audience for calls to this service. */
  serviceUrl: string;
  feedbackUrl: string;
}

export interface CdsDiscoveryInfo {
  /** No trailing slash. */
  publicBaseUrl: string;
  /** False when the API fell back to the request's own origin. */
  isConfigured: boolean;
  configurationKey: string;
  discoveryUrl: string;
  services: CdsDiscoveryInfoService[];
}

// ─── Activity ───────────────────────────────────────────────────────────────────

export type CdsCallKind = 'Invoke' | 'Feedback';

export type CdsCallOutcome =
  | 'Success'
  | 'NoCards'
  | 'Degraded'
  | 'PreconditionFailed'
  | 'Unauthorized'
  | 'Forbidden'
  | 'NotFound'
  | 'BadRequest'
  | 'Error';

/** One CDS call. Carries no patient data by design. */
export interface CdsCallLog {
  id: string;
  occurredOnUtc: string;
  kind: CdsCallKind;
  serviceId: string;
  serviceInstanceId: string | null;
  serviceTitle: string | null;
  handlerKey: string | null;
  hook: string | null;
  hookInstance: string | null;
  issuer: string | null;
  trustedIssuerId: string | null;
  trustedIssuerName: string | null;
  outcome: CdsCallOutcome;
  httpStatus: number;
  /** Cards returned, or feedback items received for a Feedback call. */
  cardCount: number;
  durationMs: number;
  /** Short reason, never patient data. */
  detail: string | null;
  /** Epic only: the action that fired the alert (bpa-trigger-action), e.g. "5". */
  triggerAction?: string | null;
  epicVersion?: string | null;
  criteriaId?: string | null;
  implementationVersion?: string | null;
  /** Resources fetched from the EHR's FHIR server. Null: no fetch was made. */
  callbackResourceCount?: number | null;
  callbackMs?: number | null;
  /** Calls: fixes offered across the cards. */
  suggestionsOffered?: number;
  /** Feedback: fixes the clinician accepted. */
  suggestionsAccepted?: number;
  /** Feedback: the dismiss reason codes picked (codes only, distinct). */
  overrideReasonCodes?: string[];
  /** Feedback: how many items of the call (Epic batches feedback) carried each reason code. */
  overrideReasonCounts?: CdsReasonCount[];
}

export interface CdsReasonCount {
  code: string;
  count: number;
}

/** The reason codes of one feedback call with their counts (falls back to 1 per code for older rows). */
export function cdsReasonCounts(call: CdsCallLog): CdsReasonCount[] {
  if (call.overrideReasonCounts?.length) return call.overrideReasonCounts;
  return (call.overrideReasonCodes ?? []).map((code) => ({ code, count: 1 }));
}

export interface CdsActivityPage {
  items: CdsCallLog[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface CdsActivityFilter {
  serviceId?: string;
  kind?: CdsCallKind;
  outcome?: CdsCallOutcome;
  fromUtc?: string;
  toUtc?: string;
  page: number;
  pageSize: number;
}

// ─── CDS Hooks 2.0 wire format ──────────────────────────────────────────────────

export interface CdsFhirAuthorization {
  access_token: string;
  token_type: string;
  expires_in: number;
  scope: string;
  subject: string;
  patient?: string;
}

/** The body an EHR posts to a service. Context and prefetch are hook-specific JSON. */
export interface CdsRequest {
  hook: string;
  hookInstance: string;
  fhirServer?: string;
  fhirAuthorization?: CdsFhirAuthorization;
  context: Record<string, unknown> | null;
  prefetch?: Record<string, unknown>;
  /** Vendor extensions, e.g. Epic's com.epic.cdshooks.request.bpa-trigger-action. */
  extension?: Record<string, unknown>;
}

export type CdsIndicator = 'info' | 'warning' | 'critical';

export interface CdsCoding {
  code: string;
  system?: string;
  display?: string;
}

export interface CdsCardSource {
  label: string;
  url?: string;
  icon?: string;
  topic?: CdsCoding;
}

export interface CdsLink {
  label: string;
  url: string;
  type: 'absolute' | 'smart';
  appContext?: string;
  /** SMART links only: the EHR may open the app without a click. */
  autolaunchable?: boolean;
}

/** One action of a suggestion. Epic supports exactly one per suggestion, of type create or delete. */
export interface CdsAction {
  type: CdsActionType | string;
  description?: string;
  /** create: the resource the EHR adds as a draft. */
  resource?: Record<string, unknown>;
  /** delete: the draft order to remove, e.g. "MedicationRequest/123". */
  resourceId?: string;
}

/** A one-click fix offered on a card. */
export interface CdsSuggestion {
  label: string;
  uuid?: string;
  isRecommended?: boolean;
  actions?: CdsAction[];
}

export interface CdsCard {
  uuid?: string;
  summary: string;
  /** Markdown. Shown as plain text in the portal. */
  detail?: string;
  indicator: CdsIndicator;
  source: CdsCardSource;
  overrideReasons?: CdsCoding[];
  links?: CdsLink[];
  suggestions?: CdsSuggestion[];
  /** "any" when the card has suggestions. */
  selectionBehavior?: string;
  extension?: Record<string, unknown>;
}

export interface CdsResponse {
  cards: CdsCard[];
}

export type CdsTestOutcome = 'Success' | 'NoCards' | 'Degraded' | 'PreconditionFailed' | 'BadRequest' | 'Error';

export interface CdsTestResult {
  outcome: CdsTestOutcome;
  httpStatus: number;
  response: CdsResponse | null;
  durationMs: number;
  messages: string[];
  resolvedSettings: Record<string, CdsSettingValue>;
}
