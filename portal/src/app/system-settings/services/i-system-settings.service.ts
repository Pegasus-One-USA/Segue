import { Observable } from 'rxjs';
import { DecryptProvisionedSecretResponse, SetSystemSettingRequest, SystemSetting } from '../models/system-setting.model';

export interface BatchSystemSettingItem {
  key: string;
  value: string;
  description: string | null;
}

export abstract class ISystemSettingsService {
  abstract getAll(): Observable<SystemSetting[]>;
  abstract set(key: string, req: SetSystemSettingRequest): Observable<SystemSetting>;
  /** Saves multiple settings in one call — e.g. every key in a General Settings group from one Edit
   *  dialog's single Update button, instead of one PUT per field. */
  abstract setBatch(items: BatchSystemSettingItem[]): Observable<SystemSetting[]>;
  abstract delete(key: string): Observable<void>;

  /** Recovery tool: decrypts a raw ProvisionedSecrets.ProtectedValue blob back to its plaintext (e.g. a private
   *  key PEM) — only works for a value encrypted by this same instance's own Data Protection key ring. */
  abstract decryptProvisionedSecret(protectedValue: string): Observable<DecryptProvisionedSecretResponse>;
}
