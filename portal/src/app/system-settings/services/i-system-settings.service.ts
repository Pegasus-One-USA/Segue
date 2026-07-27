import { Observable } from 'rxjs';
import { SetSystemSettingRequest, SystemSetting } from '../models/system-setting.model';

export abstract class ISystemSettingsService {
  abstract getAll(): Observable<SystemSetting[]>;
  abstract set(key: string, req: SetSystemSettingRequest): Observable<SystemSetting>;
  abstract delete(key: string): Observable<void>;
}
