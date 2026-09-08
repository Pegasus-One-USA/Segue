import { EnvKey, EpicEnvironment } from '../models/epic-env.model';

export const EPIC_ENV: Record<EnvKey, EpicEnvironment> = {
  sandbox: {
    label: 'Sandbox',
    base: 'https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4',
    smartConfig: 'https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4/.well-known/smart-configuration',
    token: 'https://fhir.epic.com/interconnect-fhir-oauth/oauth2/token',
    authorize: 'https://fhir.epic.com/interconnect-fhir-oauth/oauth2/authorize',
  },
  production: {
    label: 'Production',
    base: 'https://{customer}.epic.com/interconnect/api/FHIR/R4',
    smartConfig: 'https://{customer}.epic.com/interconnect/api/FHIR/R4/.well-known/smart-configuration',
    token: 'https://{customer}.epic.com/interconnect/oauth2/token',
    authorize: 'https://{customer}.epic.com/interconnect/oauth2/authorize',
  },
};
