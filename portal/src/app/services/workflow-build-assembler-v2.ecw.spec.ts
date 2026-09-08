import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { WorkflowBuildAssemblerServiceV2 } from './workflow-build-assembler-v2.service';
import { CreateSourceConnectionRequest } from './workflow-api.service';

/**
 * Regression cover for the eClinicalWorks (Healow) branch of buildSource(). This is the ONLY path that creates a
 * canvas workflow's SourceConnection — WizardServiceV2.save() deliberately doesn't call the backend in canvas mode —
 * so anything this branch drops is simply absent at run time. It previously handled only the interactive
 * audiences, which meant a Backend System (eCW "Backend — Single Patient") connection was assembled with
 * authenticationType 'None' and no signing-key reference at all, and every run failed in
 * OAuth2ClientCredentialsTokenProvider with "OAuth2 client-credentials requires a client id and secret".
 */
describe('WorkflowBuildAssemblerServiceV2 — eCW source assembly (V2)', () => {
  let service: WorkflowBuildAssemblerServiceV2;

  /** buildSource is private; specs reach it directly rather than standing up a whole canvas graph. */
  const buildSource = (
    fields: Record<string, string>,
    destinationResourceTypes: string[] = [],
  ): CreateSourceConnectionRequest =>
    (
      service as unknown as {
        buildSource: (
          f: Record<string, string>,
          d: string[],
        ) => CreateSourceConnectionRequest;
      }
    ).buildSource(fields, destinationResourceTypes);

  const backendFields = (
    overrides: Record<string, string> = {},
  ): Record<string, string> => ({
    Connector: 'Healow',
    __name: 'eCW Backend',
    'App key': 'backend-system',
    'Epic audience': 'backend-system',
    'Auth method': 'jwt',
    'Client ID': 'client-abc',
    'FHIR base URL': 'https://staging-fhir.ecwcloud.com/fhir/r4/FFBJCD',
    'Token endpoint': 'https://staging-oauthserver.ecwcloud.com/oauth/oauth2/token',
    'JWT kid': 'segue-ecw-nonprod-20260828',
    'Key vault reference': 'fhirbridge-kv',
    'Secret Name': 'ecw-signing-key',
    'JWKS URL': 'https://example.github.io/segue-jwks/jwks.json',
    'Retrieval method key': 'single-patient',
    'Patient ID / list': 'pat-42',
    ...overrides,
  });

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        WorkflowBuildAssemblerServiceV2,
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    service = TestBed.inject(WorkflowBuildAssemblerServiceV2);
  });

  // ── the bug ───────────────────────────────────────────────────────────────
  it('carries the signing-key reference for a Backend System connection', () => {
    const request = buildSource(backendFields());

    expect(request.sourceSystemType).toBe('Healow');
    expect(request.applicationType).toBe('Backend');
    expect(request.authentication.authenticationType).toBe(
      'SmartBackendServices',
    );
    expect(request.authentication.keyId).toBe('segue-ecw-nonprod-20260828');
    expect(request.authentication.privateKeyKeyVaultName).toBe('fhirbridge-kv');
    expect(request.authentication.privateKeySecretName).toBe(
      'ecw-signing-key',
    );
    expect(request.authentication.jwksUrl).toBe(
      'https://example.github.io/segue-jwks/jwks.json',
    );
  });

  it('sends no interactive payload for Backend System', () => {
    // client_credentials has no redirect, launch URL or trusted-issuer allow-list — an empty shell here used to
    // persist a redirect URI on a connection that can never use one.
    expect(buildSource(backendFields()).interactive).toBeNull();
  });

  it('carries the Single Patient retrieval config, including the Patient ID', () => {
    const retrieval = buildSource(backendFields()).retrieval;

    expect(retrieval).toBeTruthy();
    expect(retrieval!.retrievalMethod).toBe('single-patient');
    expect(retrieval!.patientIds).toEqual(['pat-42']);
  });

  it('never provisions a client secret for Backend System', () => {
    // Backend is JWT-only here; a stale 'Client Secret' left in the field bag by an earlier audience switch must
    // not create a vault reference that would then win the strategy's credential dispatch.
    const request = buildSource(
      backendFields({ 'Client Secret': 'left-over-secret' }),
    );

    expect(request.authentication.inlineClientSecret).toBeNull();
    expect(request.authentication.clientSecretKeyVaultName).toBeNull();
    expect(request.authentication.clientSecretName).toBeNull();
    expect(request.authentication.authPlacement).toBeNull();
  });

  // ── eCW's own scope vocabulary ────────────────────────────────────────────
  it('spells each system scope with the access level eCW actually publishes', () => {
    const request = buildSource(backendFields(), [
      'Patient',
      'Observation',
      'ServiceRequest',
    ]);

    // ServiceRequest is '.r'-only on eCW; the other two are '.read'. A uniform suffix here produced
    // system/ServiceRequest.read, which eCW rejects — failing the whole token request.
    expect(request.authentication.scopes).toEqual([
      'system/Patient.read',
      'system/Observation.read',
      'system/ServiceRequest.r',
    ]);
  });

  it('omits resource types eCW publishes no system read scope for', () => {
    const request = buildSource(backendFields(), ['Patient', 'Appointment']);

    expect(request.authentication.scopes).toEqual(['system/Patient.read']);
  });

  it('derives resource types from the destination selection', () => {
    const request = buildSource(backendFields(), ['Condition', 'Encounter']);

    expect(request.retrieval!.resourceTypes).toEqual([
      'Condition',
      'Encounter',
    ]);
  });

  // ── isolation: the interactive audiences must be untouched ────────────────
  it('leaves the Patient audience exactly as it was', () => {
    const request = buildSource({
      Connector: 'Healow',
      __name: 'eCW',
      'App key': 'patient-standalone',
      'Epic audience': 'patient',
      'Auth method': 'public',
      'Client ID': 'client-abc',
      'FHIR base URL': 'https://fhir.ecwcloud.com/fhir/r4/FFBJCD',
      Scopes: 'openid fhirUser launch/patient patient/Patient.read',
      'Redirect URI': 'https://app.example.com/callback',
    });

    expect(request.applicationType).toBe('Patient');
    expect(request.authentication.authenticationType).toBe('None');
    expect(request.authentication.keyId).toBeNull();
    expect(request.authentication.privateKeyKeyVaultName).toBeNull();
    // Interactive audiences keep the raw wizard scope string, not the system/ profile.
    expect(request.authentication.scopes).toEqual([
      'openid',
      'fhirUser',
      'launch/patient',
      'patient/Patient.read',
    ]);
    expect(request.interactive).toBeTruthy();
    expect(request.interactive!.redirectUris).toEqual([
      'https://app.example.com/callback',
    ]);
    expect(request.retrieval ?? null).toBeNull();
  });

  it('leaves a Provider EHR launch confidential-client connection as it was', () => {
    const request = buildSource({
      Connector: 'Healow',
      __name: 'eCW Provider',
      'App key': 'provider-ehr-launch',
      'Epic audience': 'provider-ehr-launch',
      'Auth method': 'secret',
      'Client Secret': 'real-secret',
      'Auth placement': 'basic',
      'Client ID': 'client-abc',
      'FHIR base URL': 'https://fhir.ecwcloud.com/fhir/r4/FFBJCD',
      'Trusted issuers': 'https://fhir.ecwcloud.com/fhir/r4/FFBJCD',
      'Launch display mode': 'Embedded',
    });

    expect(request.applicationType).toBe('EhrLaunch');
    expect(request.authentication.authenticationType).toBe(
      'OAuthClientCredentials',
    );
    expect(request.authentication.inlineClientSecret).toBe('real-secret');
    expect(request.authentication.authPlacement).toBe('basic');
    expect(request.interactive!.trustedIssuers).toEqual([
      'https://fhir.ecwcloud.com/fhir/r4/FFBJCD',
    ]);
    expect(request.interactive!.launchDisplayMode).toBe('Embedded');
  });
});
