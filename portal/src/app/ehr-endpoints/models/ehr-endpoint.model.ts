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

/** Vendor sandbox vs a specific customer's production instance — must match the backend's EhrEndpointType enum. */
export type EhrEndpointType = 'MyChart' | 'Epic';

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

export interface EhrEndpointFilter {
  search?: string;
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
