import { Observable } from 'rxjs';
import { AllowedCorsOrigin, CreateAllowedCorsOriginRequest } from '../models/allowed-cors-origin.model';

export abstract class IAllowedCorsOriginService {
  abstract getAll(): Observable<AllowedCorsOrigin[]>;
  abstract create(req: CreateAllowedCorsOriginRequest): Observable<AllowedCorsOrigin>;
  abstract delete(id: string): Observable<void>;
}
