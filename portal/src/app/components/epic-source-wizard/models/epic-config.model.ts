export interface FullDiscoveredValues {
  fhirBaseUrl: string;
  fhirVersion: string;
  tokenEndpoint: string;
  authzEndpoint: string;
  issuer: string;
  jwksUri: string;
  introspectEp: string;
  revokeEp: string;
  signingAlgs: string;
  pkceSupport: string;
  clientAuthMethods: string;
  smartCapabilities: string;
  supportedScopes: string;
}

export interface ConnectValues {
  appName: string;
  environment: string;
  sandboxClientId: string;
  nonProdClientId: string;
  productionClientId: string;
  organization: string;
  epicBaseUrl: string;
  fhirBaseUrl: string;
  tokenEndpoint: string;
  authzEndpoint: string;
  launchUrl: string;
  redirectUri: string;
}

export interface AuthValues {
  clientAuth: string;
  secretRef: string;
  secretStore: string;
  keySource: string;
  signingAlgorithm: string;
  jwksUrl: string;
  keyId: string;
  keyVaultRef: string;
  jwksMethod: string;
}

export type CheckStatus = 'pending' | 'ok' | 'error';

export interface CheckItem {
  label: string;
  status: CheckStatus;
}

export const CONFIG_CHECKS: CheckItem[] = [
  { label: 'App Name configured', status: 'pending' },
  { label: 'Client ID present (sandbox)', status: 'pending' },
  { label: 'SMART Discovery completed', status: 'pending' },
  { label: 'Token endpoint resolved', status: 'pending' },
  { label: 'Authorization endpoint resolved', status: 'pending' },
  { label: 'Launch URL configured', status: 'pending' },
  { label: 'Redirect URI configured', status: 'pending' },
  { label: 'At least one FHIR resource selected', status: 'pending' },
];

export const RUNTIME_CHECKS: CheckItem[] = [
  { label: 'SMART configuration endpoint reachable', status: 'pending' },
  { label: 'EHR launch sequence initiated', status: 'pending' },
  { label: 'Authorization code received', status: 'pending' },
  { label: 'Token exchange succeeded', status: 'pending' },
  { label: 'Patient context resolved', status: 'pending' },
  { label: 'FHIR resource request returned 200', status: 'pending' },
];

export const WIZARD_RESOURCES: string[] = [
  'Patient', 'Encounter', 'Observation', 'Condition', 'MedicationRequest',
  'AllergyIntolerance', 'Immunization', 'Procedure', 'DiagnosticReport',
  'DocumentReference', 'Practitioner', 'PractitionerRole',
];
