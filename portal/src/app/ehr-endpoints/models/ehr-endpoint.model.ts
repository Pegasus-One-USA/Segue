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
}

export interface EhrEndpointRequest {
  vendor: EhrVendor;
  vendorEndpointId: string;
  name: string;
  fhirBaseUrl: string;
  formatType: string;
  status: string;
}
