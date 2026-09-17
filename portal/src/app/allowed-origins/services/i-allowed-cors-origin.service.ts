import { Observable } from 'rxjs';
import {
  AllowedCorsOrigin,
  AllowedCorsOriginFilter,
  AllowedCorsOriginPage,
  CreateAllowedCorsOriginRequest,
  UpdateAllowedCorsOriginRequest,
} from '../models/allowed-cors-origin.model';

export abstract class IAllowedCorsOriginService {
  abstract getAll(): Observable<AllowedCorsOrigin[]>;
  abstract getPaged(filter: AllowedCorsOriginFilter, silent?: boolean): Observable<AllowedCorsOriginPage>;
  abstract create(req: CreateAllowedCorsOriginRequest): Observable<AllowedCorsOrigin>;
  abstract update(id: string, req: UpdateAllowedCorsOriginRequest): Observable<AllowedCorsOrigin>;
  abstract delete(id: string): Observable<void>;
  /** Forces every running replica to pick up the current rows immediately — see the backend endpoint's remarks. */
  abstract reload(): Observable<void>;
}
