import { Observable } from 'rxjs';
import { AllowedCorsOrigin, CreateAllowedCorsOriginRequest, UpdateAllowedCorsOriginRequest } from '../models/allowed-cors-origin.model';

export abstract class IAllowedCorsOriginService {
  abstract getAll(): Observable<AllowedCorsOrigin[]>;
  abstract create(req: CreateAllowedCorsOriginRequest): Observable<AllowedCorsOrigin>;
  abstract update(id: string, req: UpdateAllowedCorsOriginRequest): Observable<AllowedCorsOrigin>;
  abstract delete(id: string): Observable<void>;
}
