/** Matches the API's ReturnUrlMatchMode enum (serialized as a string). */
export type ReturnUrlMatchMode = 'Exact' | 'Domain';

/** Matches the API's ApiClientReturnUrlDto shape exactly. */
export interface ApiClientReturnUrl {
  id: string;
  url: string;
  label: string | null;
  matchMode: ReturnUrlMatchMode;
}

/** Matches the API's ApiClientDto shape exactly, so no DTO<->model mapping is needed. */
export interface ApiClient {
  id: string;
  name: string;
  clientId: string;
  isEnabled: boolean;
  lastUsedOnUtc: string | null;
  returnUrls: ApiClientReturnUrl[];
  createdOnUtc: string;
  createdBy: string | null;
}

export interface AddApiClientReturnUrlRequest {
  url: string;
  label: string | null;
  matchMode: ReturnUrlMatchMode;
}

/** One server-side page of clients. Mirrors the API's PagedResult<ApiClientDto>. */
export interface ApiClientPage {
  items: ApiClient[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface ApiClientFilter {
  search?: string;
  page: number;
  pageSize: number;
}

export interface CreateApiClientRequest {
  name: string;
}

export interface UpdateApiClientRequest {
  name: string;
  isEnabled: boolean;
}

/** Returned only from create/regenerate — the one and only time the plaintext secret is ever sent. */
export interface ApiClientCredential {
  client: ApiClient;
  plaintextSecret: string;
}
