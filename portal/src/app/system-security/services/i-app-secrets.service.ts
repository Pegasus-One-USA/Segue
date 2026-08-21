import { Observable } from 'rxjs';
import { AppSecret } from '../models/app-secret.model';

export abstract class IAppSecretsService {
  abstract getAll(): Observable<AppSecret[]>;
  abstract regenerate(secretName: string): Observable<AppSecret>;
}
