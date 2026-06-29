import { Injectable } from '@angular/core';
import { EpicApp } from '../models/epic-app.model';

@Injectable({ providedIn: 'root' })
export class ScopeBuilderService {
  buildScopes(app: EpicApp, resources: string[]): string {
    const pfx = app.scopePrefix;
    const resScopes = resources.map(r => `${pfx}${r}.read`);

    if (!app.interactive) {
      return resScopes.join(' ');
    }

    const launch = app.ehrLaunch ? 'launch' : 'launch/patient';
    return [...resScopes, 'openid', 'fhirUser', launch, 'offline_access'].join(' ');
  }
}
