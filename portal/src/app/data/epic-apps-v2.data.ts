import { AppKey, EpicApp } from '../models/epic-app.model';

export const EPIC_APPS: Record<AppKey, EpicApp> = {
  'patient-standalone': {
    label: 'Patient-facing app (standalone)',
    context: 'Patient (standalone)',
    authFlow: 'OAuth 2.0 Authorization Code + PKCE',
    scopePrefix: 'patient/',
    interactive: true,
    ehrLaunch: false,
    sandboxClientId: '0184f963-patient-sbx',
    prodClientId: '9c21ee07-patient-prod',
  },
  'provider-ehr-launch': {
    label: 'Provider app (EHR launch)',
    context: 'Provider (EHR launch)',
    authFlow: 'SMART EHR launch (Auth Code + PKCE)',
    scopePrefix: 'user/',
    interactive: true,
    ehrLaunch: true,
    sandboxClientId: '7a21cd55-prov-launch-sbx',
    prodClientId: 'b73f1a22-prov-launch-prod',
  },
  'provider-standalone': {
    label: 'Provider app (standalone)',
    context: 'Provider (standalone)',
    authFlow: 'OAuth 2.0 Authorization Code + PKCE',
    scopePrefix: 'user/',
    interactive: true,
    ehrLaunch: false,
    sandboxClientId: '3c55ee10-prov-std-sbx',
    prodClientId: 'd90a7c31-prov-std-prod',
  },
  'backend-system': {
    label: 'Backend system (server-to-server)',
    context: 'Backend system',
    authFlow: 'SMART Backend Services (signed JWT, client-credentials)',
    scopePrefix: 'system/',
    interactive: false,
    ehrLaunch: false,
    sandboxClientId: '9f0e2b44-backend-sbx',
    prodClientId: 'e15c8d90-backend-prod',
  },
};
