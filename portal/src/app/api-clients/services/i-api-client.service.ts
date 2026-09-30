import { Observable } from 'rxjs';
import {
  AddApiClientReturnUrlRequest,
  ApiClient,
  ApiClientCredential,
  ApiClientFilter,
  ApiClientPage,
  CreateApiClientRequest,
  UpdateApiClientRequest,
} from '../models/api-client.model';

export abstract class IApiClientService {
  abstract getPaged(filter: ApiClientFilter, silent?: boolean): Observable<ApiClientPage>;
  abstract create(req: CreateApiClientRequest): Observable<ApiClientCredential>;
  abstract regenerateSecret(id: string): Observable<ApiClientCredential>;
  abstract update(id: string, req: UpdateApiClientRequest): Observable<ApiClient>;
  abstract delete(id: string): Observable<void>;
  abstract addReturnUrl(id: string, req: AddApiClientReturnUrlRequest): Observable<ApiClient>;
  abstract removeReturnUrl(id: string, returnUrlId: string): Observable<ApiClient>;
}
