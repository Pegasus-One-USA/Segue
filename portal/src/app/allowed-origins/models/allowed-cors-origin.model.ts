/** Matches the API's AllowedCorsOriginDto shape exactly, so no DTO↔model mapping is needed. */
export interface AllowedCorsOrigin {
  id: string;
  originUrl: string;
  label: string | null;
  createdOnUtc: string;
  createdBy: string | null;
}

/** One server-side page of origins. Mirrors the API's PagedResult<AllowedCorsOriginDto>. */
export interface AllowedCorsOriginPage {
  items: AllowedCorsOrigin[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface AllowedCorsOriginFilter {
  search?: string;
  page: number;
  pageSize: number;
}

export interface CreateAllowedCorsOriginRequest {
  originUrl: string;
  label: string | null;
}

export interface UpdateAllowedCorsOriginRequest {
  originUrl: string;
  label: string | null;
}
