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

    // 'launch' honors an EHR-supplied launch context; 'launch/patient' asks Epic to show its own
    // patient picker for patient-context standalone apps. Provider Standalone needs neither — it
    // gets direct user-level access and does its own patient search inside FHIRBridge.
    const launch = app.ehrLaunch
      ? 'launch'
      : app.scopePrefix === 'patient/'
        ? 'launch/patient'
        : null;
    return [...resScopes, 'openid', 'fhirUser', launch, 'offline_access']
      .filter((s): s is string => s !== null)
      .join(' ');
  }
}
