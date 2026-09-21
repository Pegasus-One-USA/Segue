// Mirrors FHIRBridge.Application.Abstractions.Licensing.LicenseState (LicenseController serializes it
// via `.ToString()` on LicenseStatusDto, not through the JsonStringEnumConverter, but the wire value is
// still one of these five names).
export type LicenseState = 'Unlicensed' | 'Active' | 'Grace' | 'Expired' | 'Invalid';

/** Mirrors AllowedHospitalDto — one hospital/organization endpoint (vendor + FHIR base URL) a license
 *  permits connecting to. */
export interface AllowedHospital {
  vendor: string;
  baseUrl: string;
  displayName: string | null;
}

/** Sentinel used by every numeric field on `LicenseLimits` to mean "unlimited for that dimension" —
 *  mirrors the backend's `LicenseLimits.Unlimited`. */
export const LICENSE_UNLIMITED = -1;

/** Mirrors LicenseLimitsDto — every numeric field uses `LICENSE_UNLIMITED` (-1) to mean unlimited for that
 *  dimension; `null`/empty on a list-shaped field means unrestricted for that dimension. */
export interface LicenseLimits {
  maxUsers: number;
  maxWorkflows: number;
  maxSourceConnections: number;
  /** SourceSystemType member names (e.g. "Epic", "Healow") this license permits configuring. `null`/empty
   *  means every source type is allowed. */
  allowedSourceTypes: string[] | null;
  /** Specific hospital endpoints this license permits connecting to. `null`/empty means any hospital is
   *  allowed. */
  allowedHospitals: AllowedHospital[] | null;
  /** `LICENSE_UNLIMITED` means unlimited. No live "processed this month" counter exists yet — this is a
   *  bare limit. */
  maxProcessedRecordsPerMonth: number;
  /** FHIR resource type names (from `SupportedFhirResourceTypes.All`, e.g. "Patient", "Observation") this
   *  license permits processing. `null`/empty means every resource type is allowed. */
  allowedResourceTypes: string[] | null;
  /** Destination type names (from `DestinationType`, e.g. "SqlServer", "Sftp") this license permits
   *  writing to. `null`/empty means every destination type is allowed. */
  allowedDestinationTypes: string[] | null;
  /** Hard cap on successful pipeline/workflow executions (both planes combined) per calendar month.
   *  `LICENSE_UNLIMITED` means unlimited. */
  maxSuccessfulWorkflowExecutionsPerMonth: number;
}

/** Mirrors LicenseUsageDto — live counts for the same four dimensions as LicenseLimits, plus the
 *  current-calendar-month successful-execution count that maxSuccessfulWorkflowExecutionsPerMonth caps. */
export interface LicenseUsage {
  userCount: number;
  sourceConnectionCount: number;
  tenantCount: number;
  workflowCount: number;
  successfulExecutionsThisMonth: number;
}

/** Mirrors LicenseStatusDto, the wire shape returned by GET/POST api/v1/license. */
export interface LicenseStatus {
  state: LicenseState;
  customerName: string | null;
  edition: string | null;
  issuedUtc: string | null;
  expiresUtc: string | null;
  limits: LicenseLimits | null;
  features: string[];
  invalidReason: string | null;
  isPresent: boolean;
  isExpired: boolean;
  daysRemaining: number | null;
  /** Only populated when `isPresent` — nothing meaningful to show usage against otherwise. */
  usage: LicenseUsage | null;
  /** Only ever `true` on the response to `POST /api/v1/license`, and only when the exact same token as
   *  the currently-active license was submitted again — nothing was re-persisted and no new history row
   *  was added. Always `false` on `GET /api/v1/license`. */
  alreadyActive: boolean;
}

/** Body of `POST /api/v1/license`. */
export interface ApplyLicenseRequest {
  token: string;
}

/** Mirrors LicenseHistoryEntryDto, one row from `GET /api/v1/license/history` — every license this
 *  install has ever successfully applied, newest first. */
export interface LicenseHistoryEntry {
  appliedUtc: string;
  customerName: string | null;
  edition: string | null;
  state: string;
  expiresUtc: string | null;
  /** True for exactly one row — whichever token matches the currently-active license. */
  isCurrent: boolean;
}

/** Lifecycle of this install's own outbound license request — mirrors the backend's
 *  `LicenseRequestStatus` enum. */
export type LicenseRequestState = 'Pending' | 'Submitted' | 'Failed';

/** Mirrors LicenseRequestStatusResult, one row from `GET /api/v1/license-request` — this install can have
 *  any number of these, each independent and identified by `id`. */
export interface LicenseRequestStatus {
  id: string;
  clientName: string;
  email: string;
  companyName: string | null;
  address: string | null;
  phoneNumber: string;
  status: LicenseRequestState;
  createdUtc: string;
  lastAttemptUtc: string | null;
  submissionError: string | null;
  /** Populated only when `status` is 'Failed' — a single copy-pasteable string to share with the
   *  licensor manually instead of the direct API call that didn't succeed. */
  encodedPayload: string | null;
  /** The admin's own browser origin (`window.location.origin`) at the most recent submission —
   *  informational only, shown to the licensor, never back to this install. */
  requestHost: string | null;
}

/** Body of `POST /api/v1/license-request` (the blank first-time request form) and, unchanged, of
 *  `PUT /api/v1/license-request` (editing an existing request's details). */
export interface CreateLicenseRequestRequest {
  clientName: string;
  email: string;
  companyName: string | null;
  address: string | null;
  phoneNumber: string;
  /** `window.location.origin` at submit time — the URL this admin was actually looking at, which the API
   *  itself has no way to know (its own Host header only reflects where the API is bound, e.g.
   *  localhost:5000, never the portal's own port). Purely informational for the licensor; never shown
   *  back to this install. */
  requestedFromUrl: string;
}

/** Mirrors LicensorApplicationUrlDto, the wire shape for GET/PUT
 *  `api/v1/license-request/licensor-url` — the one system setting reachable while unlicensed. */
export interface LicensorApplicationUrl {
  url: string;
}
