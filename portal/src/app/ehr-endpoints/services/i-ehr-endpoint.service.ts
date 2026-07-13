import { Observable } from 'rxjs';
import { EhrEndpoint, EhrEndpointRequest } from '../models/ehr-endpoint.model';

export abstract class IEhrEndpointService {
  abstract getAll(): Observable<EhrEndpoint[]>;
  abstract getById(id: string): Observable<EhrEndpoint>;
  abstract create(req: EhrEndpointRequest): Observable<EhrEndpoint>;
  abstract update(id: string, req: EhrEndpointRequest): Observable<EhrEndpoint>;
  abstract delete(id: string): Observable<void>;
}
