import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { WorkflowBuildAssemblerServiceV2 } from './workflow-build-assembler-v2.service';
import { CreateSourceConnectionRequest } from './workflow-api.service';

/**
 * Cross-vendor cover for the authentication block buildSource() assembles, across the vendor x audience x
 * auth-method matrix the shared EHR-vendor source form can actually produce.
 *
 * Why this exists: creating a source connection on the fly from the workflow canvas and creating one from
 * Settings -> Source Connections ("master", WizardServiceV2.save()) are two independent builders over the SAME
 * wizard field bag. The master path derives every credential field from the selected Auth Method; buildSource()
 * used to re-derive it per vendor branch, and the three copies had drifted apart:
 *
 *  - athenahealth ignored 'Auth method' and never emitted keyId/privateKey*, so a Backend System connection
 *    registered for private_key_jwt persisted with no signing key AND no secret. At run time
 *    BackendServicesApplicationStrategy.UsesJwtAssertion() tests PrivateKeyPem, found none, and routed to the
 *    client-secret provider, which had nothing either - the reported "works from master, fails from workflow".
 *  - Epic emitted keyId/privateKey* ungated, so an interactive (PKCE) connection persisted stale key material.
 *  - jwksUrl was never sent by ANY branch, and since ConfigurationService.PreserveSecretsIfBlank guards only
 *    ClientSecret/PrivateKey, every workflow rebuild silently nulled it on an existing row.
 *
 * These assert the credential fields follow the selected Auth Method rather than the vendor, so a future vendor
 * branch that hand-rolls its own auth block instead of calling buildAuthentication() fails here.
 */
describe('WorkflowBuildAssemblerServiceV2 - source authentication (V2)', () => {
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

  /** The key material the form writes when Auth Method = JWT / Private Key (JWKS). */
  const jwtFields = {
    'JWT kid': 'kid-123',
    'Key vault reference': 'fhirbridge-kv',
    'Secret Name': 'signing-key',
    'JWKS URL': 'https://example.org/jwks.json',
  };

  const athenaBackend = (
    overrides: Record<string, string> = {},
  ): Record<string, string> => ({
    Connector: 'Athenahealth',
    __name: 'Athena Backend',
    'App key': 'backend-system',
    'Client ID': 'athena-client',
    'FHIR base URL': 'https://api.preview.platform.athenahealth.com/fhir/r4',
    'Token endpoint':
      'https://api.preview.platform.athenahealth.com/oauth2/v1/token',
    'Practice ID': '195900',
    'Retrieval method key': 'search-rest',
    'Retrieval resource type': 'Patient',
    ...jwtFields,
    ...overrides,
  });

  const epicFields = (
    overrides: Record<string, string> = {},
  ): Record<string, string> => ({
    Connector: 'Epic',
    __name: 'Epic Source',
    'App key': 'backend-system',
    'Client ID': 'epic-client',
    'FHIR base URL': 'https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4',
    'Token endpoint':
      'https://fhir.epic.com/interconnect-fhir-oauth/oauth2/token',
    Scopes: 'system/Patient.read',
    'Retrieval method key': 'search-rest',
    'Retrieval resource type': 'Patient',
    ...jwtFields,
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

  // -- athenahealth: the reported failure ------------------------------------
  describe('athenahealth + Backend System', () => {
    it('carries the signing-key reference when Auth Method is JWT', () => {
      const request = buildSource(athenaBackend({ 'Auth method': 'jwt' }));

      expect(request.sourceSystemType).toBe('Athenahealth');
      expect(request.applicationType).toBe('Backend');
      // The field that actually decides the run: SourceConnectionRuntimeResolver resolves this reference into
      // PrivateKeyPem, which is what BackendServicesApplicationStrategy.UsesJwtAssertion() dispatches on.
      expect(request.authentication.privateKeyKeyVaultName).toBe(
        'fhirbridge-kv',
      );
      expect(request.authentication.privateKeySecretName).toBe('signing-key');
      expect(request.authentication.keyId).toBe('kid-123');
      expect(request.authentication.jwksUrl).toBe(
        'https://example.org/jwks.json',
      );
      expect(request.authentication.authenticationType).toBe(
        'SmartBackendServices',
      );
      // ah-practice scoping is required on every athenahealth request regardless of auth method.
      expect(request.authentication.practiceId).toBe('195900');
    });

    it('carries the client secret when Auth Method is Client Secret', () => {
      const request = buildSource(
        athenaBackend({ 'Auth method': 'secret', 'Client Secret': 's3cret' }),
      );

      expect(request.authentication.authenticationType).toBe(
        'OAuthClientCredentials',
      );
      expect(request.authentication.inlineClientSecret).toBe('s3cret');
      expect(request.authentication.clientSecretKeyVaultName).toBe(
        'workflow-secrets',
      );
      expect(request.authentication.clientSecretName).toBeTruthy();
      // No signing key on a client-secret app - emitting one would make UsesJwtAssertion() pick the wrong provider.
      expect(request.authentication.keyId).toBeNull();
      expect(request.authentication.privateKeyKeyVaultName).toBeNull();
      expect(request.authentication.jwksUrl).toBeNull();
    });

    it('defaults Backend System to Client Secret when Auth Method is absent', () => {
      // Mirrors the form's own defaultAuthMethodFor(): athenahealth Backend defaults to Client Secret. Covers a
      // node saved before 'Auth method' was captured.
      const fields = athenaBackend();
      delete fields['Auth method'];
      const request = buildSource(fields);

      expect(request.authentication.authenticationType).toBe(
        'OAuthClientCredentials',
      );
    });

    it('omits the authorize endpoint for Backend System', () => {
      // client_credentials never performs a browser redirect, so there is no authorize endpoint to store -
      // master gates this on the audience, and this branch previously sent it regardless.
      const request = buildSource(
        athenaBackend({
          'Auth method': 'jwt',
          'Authorize endpoint': 'https://example.org/authorize',
        }),
      );

      expect(request.authentication.authorizationEndpoint).toBeNull();
    });
  });

  // -- Epic ------------------------------------------------------------------
  describe('Epic', () => {
    it('pins Backend System to SMART Backend Services and still carries the key', () => {
      // ConfigurationService.ValidateEpicSourceConnection rejects anything else for a Backend Epic source, so the
      // Auth Method dropdown must not be able to override it here.
      //
      // The key material assertions are the point: pinning authenticationType while gating the signing key on the
      // RAW auth method would persist SmartBackendServices with no key and no secret - the same unrunnable
      // combination this suite exists to prevent, just reintroduced for Epic. Key material must follow the
      // RESOLVED authentication type.
      const request = buildSource(
        epicFields({ 'Auth method': 'secret', 'Client Secret': 's3cret' }),
      );

      expect(request.authentication.authenticationType).toBe(
        'SmartBackendServices',
      );
      expect(request.authentication.keyId).toBe('kid-123');
      expect(request.authentication.privateKeyKeyVaultName).toBe(
        'fhirbridge-kv',
      );
      expect(request.authentication.privateKeySecretName).toBe('signing-key');
      expect(request.authentication.jwksUrl).toBe(
        'https://example.org/jwks.json',
      );
      // A type that signs a JWT assertion never sends a client secret, so a secret typed before the audience was
      // switched must not ride along - OAuth2ClientCredentialsTokenProvider would never be reached to use it.
      expect(request.authentication.inlineClientSecret).toBeNull();
      expect(request.authentication.clientSecretKeyVaultName).toBeNull();
      expect(request.authentication.authPlacement).toBeNull();
    });

    it('carries the signing key when Auth Method is absent on Backend System', () => {
      // The symmetric case to athenahealth's "defaults to Client Secret" spec, and the one that actually bites:
      // the form writes `v.authMethod ?? defaultAuthMethodFor(...)`, but a node saved before that field existed
      // arrives with no 'Auth method' at all. Epic Backend resolves to SmartBackendServices regardless, so the
      // key must still be carried rather than nulled by a 'secret' default.
      const fields = epicFields();
      delete fields['Auth method'];
      const request = buildSource(fields);

      expect(request.authentication.authenticationType).toBe(
        'SmartBackendServices',
      );
      expect(request.authentication.keyId).toBe('kid-123');
      expect(request.authentication.privateKeyKeyVaultName).toBe(
        'fhirbridge-kv',
      );
      expect(request.authentication.privateKeySecretName).toBe('signing-key');
      // Rebuilding an existing workflow must not clear the stored JWKS URL: PreserveSecretsIfBlank guards only
      // ClientSecret/PrivateKey, so a null sent here overwrites whatever was persisted.
      expect(request.authentication.jwksUrl).toBe(
        'https://example.org/jwks.json',
      );
    });

    it('carries the signing key and JWKS URL for Backend System', () => {
      const request = buildSource(epicFields({ 'Auth method': 'jwt' }));

      expect(request.authentication.keyId).toBe('kid-123');
      expect(request.authentication.privateKeyKeyVaultName).toBe(
        'fhirbridge-kv',
      );
      expect(request.authentication.privateKeySecretName).toBe('signing-key');
      expect(request.authentication.jwksUrl).toBe(
        'https://example.org/jwks.json',
      );
    });

    it('drops stale key material on a public (PKCE) interactive connection', () => {
      // The form leaves the JWT fields populated when switching audience/auth method; emitting them ungated (as
      // this branch used to) persisted signing credentials onto a public client that never signs anything.
      const request = buildSource(
        epicFields({
          'App key': 'patient-standalone',
          'Auth method': 'public',
          'Redirect URI': 'https://portal.example.org/callback',
        }),
      );

      expect(request.applicationType).toBe('Patient');
      expect(request.authentication.authenticationType).toBe('None');
      expect(request.authentication.keyId).toBeNull();
      expect(request.authentication.privateKeyKeyVaultName).toBeNull();
      expect(request.authentication.privateKeySecretName).toBeNull();
      expect(request.authentication.jwksUrl).toBeNull();
    });
  });

  // -- applies to every vendor -----------------------------------------------
  describe('shared behaviour', () => {
    it('only sets authPlacement for Client Secret auth', () => {
      const secret = buildSource(
        athenaBackend({
          'Auth method': 'secret',
          'Client Secret': 's3cret',
          'Auth placement': 'basic',
        }),
      );
      expect(secret.authentication.authPlacement).toBe('basic');

      // A JWT app has no client id/secret to place, so master sends null rather than a meaningless default.
      const jwt = buildSource(
        athenaBackend({ 'Auth method': 'jwt', 'Auth placement': 'basic' }),
      );
      expect(jwt.authentication.authPlacement).toBeNull();
    });

    it('sends null secret references when the secret box is left blank', () => {
      // ConfigurationService.PreserveSecretsIfBlank reads nulls as "keep whatever is already stored" - sending a
      // freshly minted reference instead would orphan the existing secret on every workflow rebuild.
      const request = buildSource(athenaBackend({ 'Auth method': 'secret' }));

      expect(request.authentication.inlineClientSecret).toBeNull();
      expect(request.authentication.clientSecretKeyVaultName).toBeNull();
      expect(request.authentication.clientSecretName).toBeNull();
    });
  });
});
