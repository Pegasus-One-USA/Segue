export type AppKey =
  | 'patient-standalone'
  | 'provider-ehr-launch'
  | 'provider-standalone'
  | 'backend-system';

export interface EpicApp {
  label: string;
  context: string;
  authFlow: string;
  scopePrefix: string;
  interactive: boolean;
  ehrLaunch: boolean;
  sandboxClientId: string;
  prodClientId: string;
}
