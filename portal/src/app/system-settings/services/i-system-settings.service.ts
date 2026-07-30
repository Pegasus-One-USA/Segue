import { Observable } from 'rxjs';
import { DecryptProvisionedSecretResponse, SetSystemSettingRequest, SystemSetting } from '../models/system-setting.model';

export abstract class ISystemSettingsService {
  abstract getAll(): Observable<SystemSetting[]>;
  abstract set(key: string, req: SetSystemSettingRequest): Observable<SystemSetting>;
  abstract delete(key: string): Observable<void>;

  /** Recovery tool: decrypts a raw ProvisionedSecrets.ProtectedValue blob back to its plaintext (e.g. a private
   *  key PEM) — only works for a value encrypted by this same instance's own Data Protection key ring. */
  abstract decryptProvisionedSecret(protectedValue: string): Observable<DecryptProvisionedSecretResponse>;
}
