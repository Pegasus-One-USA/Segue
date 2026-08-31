/** Matches the API's AllowedCorsOriginDto shape exactly, so no DTO↔model mapping is needed. */
export interface AllowedCorsOrigin {
  id: string;
  originUrl: string;
  label: string | null;
  createdOnUtc: string;
  createdBy: string | null;
}

export interface CreateAllowedCorsOriginRequest {
  originUrl: string;
  label: string | null;
}

export interface UpdateAllowedCorsOriginRequest {
  originUrl: string;
  label: string | null;
}
