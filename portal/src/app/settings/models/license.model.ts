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

/** Sentinel used by every numeric field on `LicenseLimits`/`DevLicenseMintRequest` to mean "unlimited for
 *  that dimension" — mirrors the backend's `LicenseLimits.Unlimited`. */
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
}

/** Body of `POST /api/v1/license`. */
export interface ApplyLicenseRequest {
  token: string;
}

// ─── ⚠ TEMPORARY / DEV-ONLY — backs the "Dev: Mint a test license" page (settings/license/mint-dev) ───
// Delete these two types alongside that page, LicenseService.mintDev(), and LICENSE_ENDPOINTS.devMint
// once license minting moves to its own separate internal tool. Mirrors the backend's
// DevLicenseMintRequestDto/DevLicenseMintResponse (DevLicenseMintingController) field for field.

/** Body of the temporary `POST /api/v1/dev/license-mint` endpoint. Every numeric limit uses
 *  `LICENSE_UNLIMITED` (-1) for unlimited; an empty `features`/`allowedSourceTypes`/`allowedHospitals` list
 *  means no extra features / unrestricted for that dimension. */
export interface DevLicenseMintRequest {
  customerId: string;
  customerName: string | null;
  edition: string | null;
  expiresUtc: string;
  maxUsers: number;
  maxWorkflows: number;
  maxSourceConnections: number;
  features: string[];
  allowedSourceTypes: string[];
  allowedHospitals: AllowedHospital[];
  maxProcessedRecordsPerMonth: number | null;
  allowedResourceTypes: string[];
  allowedDestinationTypes: string[];
  maxSuccessfulWorkflowExecutionsPerMonth: number;
  /** Minutes after minting this token must be applied within, or it's rejected as expired. `null` means no
   *  activation deadline. Never affects an already-applied, currently-running license. */
  activationWindowMinutes: number | null;
}

/** Response of the temporary `POST /api/v1/dev/license-mint` endpoint. */
export interface DevLicenseMintResponse {
  token: string;
}
