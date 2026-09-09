/** Vendor axis — must match the backend's SourceSystemType enum member names (serialized as strings). */
export type EhrVendor =
  | 'Sample'
  | 'Epic'
  | 'Cerner'
  | 'GenericFhir'
  | 'Athenahealth'
  | 'Allscripts'
  | 'Hl7v2'
  | 'Healow'
  | 'MeditechGreenfield'
  | 'NewEHR'
  | 'NewEHRTwo';

/** Vendor sandbox vs a specific customer's production instance — must match the backend's EhrEndpointType enum.
 *  'Epic' and 'Ecw' are vendor-sandbox rows (Provider Standalone audience); 'MyChart' is a customer production
 *  instance (Patient Standalone audience). */
export type EhrEndpointType = 'MyChart' | 'Epic' | 'Ecw';

/** Matches the API's EhrEndpointDto shape exactly, so no DTO↔model mapping is needed. */
export interface EhrEndpoint {
  id: string;
  vendor: EhrVendor;
  vendorEndpointId: string;
  name: string;
  fhirBaseUrl: string;
  formatType: string;
  status: string;
  createdOnUtc: string;
  createdBy: string | null;
  modifiedOnUtc: string | null;
  modifiedBy: string | null;
  endpointType: EhrEndpointType;
}

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

/**
 * One page of the EHR Endpoints listing. `availableVendors` is the distinct set of vendors that actually have
 * endpoint rows, computed server-side over the UNFILTERED set — the same facet contract the Workflows and
 * Execution History lists use for their Source filters, so all three screens offer only vendors with real data
 * behind them rather than every SourceSystemType the platform supports.
 */
export interface EhrEndpointPage {
  items: EhrEndpoint[];
  totalCount: number;
  page: number;
  pageSize: number;
  availableVendors: EhrVendor[];
}

export interface EhrEndpointFilter {
  search?: string;
  /** The "Source" dropdown — the row's Vendor (backend SourceSystemType). */
  vendor?: EhrVendor;
  /** The "Status" dropdown: true = Status is "active", false = anything else. Mirrors the Status column's
   *  own active-or-not split, since Status is a free-text string rather than a closed enum. */
  isActive?: boolean;
  sortDescending?: boolean;
  page: number;
  pageSize: number;
}

export interface EhrEndpointRequest {
  vendor: EhrVendor;
  vendorEndpointId: string;
  name: string;
  fhirBaseUrl: string;
  formatType: string;
  status: string;
  endpointType: EhrEndpointType;
}
