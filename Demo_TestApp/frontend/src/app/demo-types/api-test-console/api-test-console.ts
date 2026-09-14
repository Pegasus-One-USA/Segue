import { Component, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../../environments/environment';

const BACKEND_BASE_URL = environment.healthAppBase;

/** Matches ApiTestCallEntity (backend/ApiEndpointTestEntities.cs) exactly, so no DTO↔model mapping is needed. */
interface ApiTestCall {
  id: number;
  apiName: string;
  httpMethod: string;
  content: string;
  authMode: string | null;
  isValid: boolean;
  errorMessage: string | null;
  receivedOnUtc: string;
}

interface SampleApi {
  /** Matches ApiEndpointTestEndpoints' ApiName constants exactly — used both to label a call row and to filter. */
  apiName: string;
  title: string;
  method: string;
  path: string;
  description: string;
  /** Which FHIRBridge ApiEndpoint destination "Payload shape" setting this API matches. */
  matchingPayloadShape: string;
  validExample: string;
  invalidExample: string;
  invalidReason: string;
  requiredFields: string;
}

// One entry per sample API in backend/ApiEndpointTestEndpoints.cs — kept in sync with it by hand, same as every
// other "matches the backend" comment convention in this app.
const SAMPLE_APIS: SampleApi[] = [
  {
    apiName: 'SingleRecord',
    title: '1. Single object — Create Patient Record',
    method: 'POST',
    path: '/api/apitest/single-record',
    description: 'Accepts exactly one JSON object per request — the shape a destination sends when its Payload '
      + 'shape is "One request per record".',
    matchingPayloadShape: 'One request per record (recordPerRequest)',
    requiredFields: 'mrn, firstName, lastName (all non-empty strings)',
    validExample: JSON.stringify(
      { mrn: 'MRN-1001', firstName: 'Alice', lastName: 'Johnson', dateOfBirth: '1985-03-14', gender: 'female' },
      null, 2),
    invalidExample: JSON.stringify({ firstName: 'Alice', lastName: 'Johnson' }, null, 2),
    invalidReason: 'Missing the required "mrn" field.',
  },
  {
    apiName: 'RecordsBatch',
    title: '2. Array of objects — Bulk Import Records',
    method: 'POST',
    path: '/api/apitest/records-batch',
    description: 'Accepts a JSON array of records in one request — the destination\'s default "JSON array" '
      + 'payload shape, batching many mapped records into a single call.',
    matchingPayloadShape: 'JSON array (jsonArray) — the default',
    requiredFields: 'A non-empty array; every element needs an "mrn" field.',
    validExample: JSON.stringify(
      [
        { mrn: 'MRN-2001', firstName: 'Bob', lastName: 'Smith' },
        { mrn: 'MRN-2002', firstName: 'Carla', lastName: 'Diaz' },
      ], null, 2),
    invalidExample: JSON.stringify([{ firstName: 'Bob', lastName: 'Smith' }], null, 2),
    invalidReason: 'Record 0 is missing the required "mrn" field.',
  },
  {
    apiName: 'RecordsEnvelope',
    title: '3. Envelope — Batch With Run Metadata',
    method: 'POST',
    path: '/api/apitest/records-envelope',
    description: 'Accepts a batch wrapped with run/route provenance metadata — the destination\'s "Envelope" '
      + 'payload shape, for an API that wants to know what a batch IS before reading what\'s in it.',
    matchingPayloadShape: 'Envelope — run metadata plus records (envelope)',
    requiredFields: 'meta.resourceType (string); records (non-empty array)',
    validExample: JSON.stringify(
      {
        meta: { resourceType: 'Patient', recordCount: 2, routeName: 'Nightly Patient Sync' },
        records: [
          { mrn: 'MRN-3001', firstName: 'Dana', lastName: 'Lee' },
          { mrn: 'MRN-3002', firstName: 'Evan', lastName: 'Wright' },
        ],
      }, null, 2),
    invalidExample: JSON.stringify({ meta: { resourceType: 'Patient' }, records: [] }, null, 2),
    invalidReason: '"records" is an empty array — at least one record is required.',
  },
  {
    apiName: 'CustomEvent',
    title: '4. Arbitrary partner shape — Custom Event (for Request Body Template)',
    method: 'POST',
    path: '/api/apitest/custom-event',
    description: 'Not one of FHIRBridge\'s own framings at all — this is what a real partner\'s own event '
      + 'contract looks like. Use it to test the ApiEndpoint destination\'s Request Body Template: paste this '
      + 'exact shape into the destination\'s "Request body template" field, with {{fieldName}} placeholders '
      + '(matching your Mapping Profile\'s field names) in place of the values below.',
    matchingPayloadShape: 'Any — pair with a Request Body Template matching this exact shape',
    requiredFields: 'eventType (string); patient.id (string)',
    validExample: JSON.stringify(
      { eventType: 'PatientUpdated', patient: { id: 'MRN-4001', fullName: 'Farah Khan', age: 42 }, source: 'FHIRBridge' },
      null, 2),
    invalidExample: JSON.stringify({ patient: { fullName: 'Farah Khan' } }, null, 2),
    invalidReason: 'Missing the required "eventType" field, and patient.id.',
  },
];

/** Every sample API and every auth-mode endpoint accepts all four — matches ApiEndpointSettings.Parse's allowed
 *  HTTP methods for the ApiEndpoint destination itself (no GET: the destination only ever pushes data out). */
const ACCEPTED_METHODS = 'POST, PUT, PATCH, DELETE';

interface AuthModeDoc {
  /** Matches ApiAuthTestEndpoints' mode constants (None/Bearer/ApiKeyHeader/...) — used to label a call row. */
  authMode: string;
  title: string;
  path: string;
  description: string;
  /** Which FHIRBridge ApiEndpoint destination "Auth mode" setting this endpoint matches. */
  matchingDestinationAuthMode: string;
  /** How to send the credential — header/query name plus the fixed value, or a longer note for HMAC/OAuth2. */
  credential: string;
  curlExample: string;
}

// One entry per mode in backend/ApiAuthTestEndpoints.cs, credentials matching backend/ApiTestAuthCredentials.cs
// exactly — kept in sync with both by hand, same convention as SAMPLE_APIS above.
const AUTH_MODE_DOCS: AuthModeDoc[] = [
  {
    authMode: 'None',
    title: 'No auth',
    path: '/api/apitest/auth/none',
    description: 'Accepts every call with no credential check at all — the destination\'s "None" auth mode.',
    matchingDestinationAuthMode: 'None',
    credential: '(none — no Authorization header or credential of any kind is required)',
    curlExample: "curl -X POST {base}/api/apitest/auth/none -H 'Content-Type: application/json' -d '{}'",
  },
  {
    authMode: 'Bearer',
    title: 'Bearer token',
    path: '/api/apitest/auth/bearer',
    description: 'Requires a fixed bearer token on every call.',
    matchingDestinationAuthMode: 'Bearer',
    credential: 'Authorization: Bearer fhirbridge-test-bearer-8f2c91a4d6e7',
    curlExample:
      "curl -X POST {base}/api/apitest/auth/bearer -H 'Authorization: Bearer fhirbridge-test-bearer-8f2c91a4d6e7' "
      + "-H 'Content-Type: application/json' -d '{}'",
  },
  {
    authMode: 'ApiKeyHeader',
    title: 'API key (header)',
    path: '/api/apitest/auth/apikey-header',
    description: 'Requires a fixed API key sent as a request header.',
    matchingDestinationAuthMode: 'API Key (header)',
    credential: 'X-Api-Key: fhirbridge-test-apikey-3b7a05c9f1e2',
    curlExample:
      "curl -X POST {base}/api/apitest/auth/apikey-header -H 'X-Api-Key: fhirbridge-test-apikey-3b7a05c9f1e2' "
      + "-H 'Content-Type: application/json' -d '{}'",
  },
  {
    authMode: 'ApiKeyQuery',
    title: 'API key (query string)',
    path: '/api/apitest/auth/apikey-query',
    description: 'Requires a fixed API key sent as a query-string parameter rather than a header.',
    matchingDestinationAuthMode: 'API Key (query string)',
    credential: '?api_key=fhirbridge-test-apikey-query-6d4e18b0a7c3',
    curlExample:
      "curl -X POST '{base}/api/apitest/auth/apikey-query?api_key=fhirbridge-test-apikey-query-6d4e18b0a7c3' "
      + "-H 'Content-Type: application/json' -d '{}'",
  },
  {
    authMode: 'Basic',
    title: 'Basic auth',
    path: '/api/apitest/auth/basic',
    description: 'Requires a fixed username/password pair sent as HTTP Basic auth.',
    matchingDestinationAuthMode: 'Basic',
    credential: 'Username: fhirbridge-test  /  Password: test-password-2c8f4a91',
    curlExample:
      "curl -X POST {base}/api/apitest/auth/basic -u 'fhirbridge-test:test-password-2c8f4a91' "
      + "-H 'Content-Type: application/json' -d '{}'",
  },
  {
    authMode: 'HmacSha256',
    title: 'HMAC-SHA256 signature',
    path: '/api/apitest/auth/hmac',
    description: 'Requires the request body signed with a fixed shared secret: HMAC-SHA256 over '
      + '"{unix timestamp}.{body}", sent as X-Signature-256: sha256=<hex> alongside X-Signature-Timestamp. '
      + 'Rejected if the timestamp is more than 5 minutes old. Cannot be exercised with a plain curl one-liner — '
      + 'the destination itself computes the signature; use FHIRBridge\'s ApiEndpoint destination configured with '
      + 'this shared secret, or a script that replicates ApiEndpointSender.ApplyHmac.',
    matchingDestinationAuthMode: 'HMAC-SHA256',
    credential: 'Shared secret: fhirbridge-test-hmac-secret-9e1d7c4b2a68',
    curlExample: '(see description — cannot be exercised with a plain curl command)',
  },
  {
    authMode: 'OAuth2ClientCredentials',
    title: 'OAuth2 client credentials',
    path: '/api/apitest/auth/oauth2',
    description: 'Requires a bearer token acquired from this app\'s own token endpoint first — point the '
      + 'destination\'s Token Endpoint URL, Client ID and Client Secret at the values below; FHIRBridge acquires '
      + 'and caches the token itself. To try it by hand, first POST to the token endpoint, then use the returned '
      + 'access_token as a Bearer token against the auth endpoint below.',
    matchingDestinationAuthMode: 'OAuth2 Client Credentials',
    credential: 'Token endpoint: {base}/api/apitest/oauth/token  •  Client ID: fhirbridge-test-client  •  '
      + 'Client Secret: fhirbridge-test-client-secret-4f6b8d2e',
    curlExample:
      "curl -X POST {base}/api/apitest/oauth/token -d grant_type=client_credentials "
      + "-d client_id=fhirbridge-test-client -d client_secret=fhirbridge-test-client-secret-4f6b8d2e\n"
      + "# then:\n"
      + "curl -X POST {base}/api/apitest/auth/oauth2 -H 'Authorization: Bearer <access_token from above>' "
      + "-H 'Content-Type: application/json' -d '{}'",
  },
  {
    authMode: 'ClientCertificate',
    title: 'Client certificate (mutual TLS)',
    path: '/api/apitest/auth/client-certificate',
    description: 'Requires a client certificate presented during the TLS handshake — this backend mints a fresh '
      + 'self-signed test certificate every time it starts, so fetch the current one (base64 PFX + password) from '
      + 'the credential endpoint below rather than using a fixed value. Paste the "formattedSecret" value straight '
      + 'into the destination\'s secret field. Only works when this app is reached directly over HTTPS by its own '
      + 'Kestrel listener, not through a reverse proxy that doesn\'t forward client certificates.',
    matchingDestinationAuthMode: 'Client Certificate (mTLS)',
    credential: '(fetched live — see "Get current test certificate" below)',
    curlExample:
      "curl -X POST https://localhost:<port>/api/apitest/auth/client-certificate --cert client.pem --key client.key "
      + "-H 'Content-Type: application/json' -d '{}'",
  },
];

/**
 * A small, self-contained console for exercising FHIRBridge's ApiEndpoint destination end to end: four sample
 * APIs (see SAMPLE_APIS above / backend/ApiEndpointTestEndpoints.cs), each expecting a different request shape,
 * plus one endpoint per auth mode the destination supports (see AUTH_MODE_DOCS above / backend/ApiAuthTestEndpoints.cs),
 * all landing in one shared table so every call — valid or not — is visible from one place.
 *
 * Reached at a dedicated path (see app.ts's isApiTestConsole), rendered BEFORE the login gate — same pattern as
 * the EHR-launch bypass: this is a developer tool independent of any HealthApp role, and the table it reads holds
 * nothing but dummy validation-test payloads, so there is nothing here a login screen would meaningfully protect.
 */
@Component({
  selector: 'app-api-test-console',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './api-test-console.html',
  styleUrl: './api-test-console.scss',
})
export class ApiTestConsoleComponent implements OnInit {
  protected readonly backendBaseUrl = BACKEND_BASE_URL;
  protected readonly sampleApis = SAMPLE_APIS;
  protected readonly authModeDocs = AUTH_MODE_DOCS;
  protected readonly acceptedMethods = ACCEPTED_METHODS;

  protected readonly activeTab = signal<'calls' | 'docs' | 'auth'>('calls');
  protected readonly calls = signal<ApiTestCall[]>([]);
  protected readonly loading = signal(false);
  protected readonly loadError = signal('');
  protected readonly apiNameFilter = signal('');
  protected readonly expandedCallId = signal<number | null>(null);

  protected readonly clientCertificate = signal<{ pfxBase64: string; password: string; thumbprint: string; formattedSecret: string } | null>(null);
  protected readonly clientCertificateError = signal('');
  protected readonly clientCertificateLoading = signal(false);

  constructor(private readonly http: HttpClient) {}

  ngOnInit(): void {
    void this.refresh();
  }

  async refresh(): Promise<void> {
    this.loading.set(true);
    this.loadError.set('');

    try {
      const filter = this.apiNameFilter();
      const url = filter
        ? `${BACKEND_BASE_URL}/api/apitest/calls?take=200&apiName=${encodeURIComponent(filter)}`
        : `${BACKEND_BASE_URL}/api/apitest/calls?take=200`;
      const rows = await firstValueFrom(this.http.get<ApiTestCall[]>(url));
      this.calls.set(rows);
    } catch {
      this.loadError.set('Could not reach the backend — is it running on ' + BACKEND_BASE_URL + '?');
    } finally {
      this.loading.set(false);
    }
  }

  setFilter(apiName: string): void {
    this.apiNameFilter.set(apiName);
    void this.refresh();
  }

  /** Substitutes this console's own backend base URL into a curl example, so what's shown is directly copy-pasteable. */
  curlFor(doc: AuthModeDoc): string {
    return doc.curlExample.split('{base}').join(BACKEND_BASE_URL);
  }

  /** ClientCertificate is the one auth mode with no fixed credential — this backend mints a fresh self-signed
   *  certificate every time it starts (see ApiTestClientCertificateStore), so the console fetches it live. */
  async fetchClientCertificate(): Promise<void> {
    this.clientCertificateLoading.set(true);
    this.clientCertificateError.set('');

    try {
      const credential = await firstValueFrom(this.http.get<{ pfxBase64: string; password: string; thumbprint: string; formattedSecret: string }>(
        `${BACKEND_BASE_URL}/api/apitest/auth/client-certificate/credential`));
      this.clientCertificate.set(credential);
    } catch {
      this.clientCertificateError.set('Could not reach the backend — is it running on ' + BACKEND_BASE_URL + '?');
    } finally {
      this.clientCertificateLoading.set(false);
    }
  }

  toggleExpanded(callId: number): void {
    this.expandedCallId.update(current => (current === callId ? null : callId));
  }

  /** Best-effort pretty-print for the stored raw content — falls back to the verbatim string when it isn't
   *  parseable JSON (a caller can POST anything, valid JSON or not). */
  prettyContent(content: string): string {
    try {
      return JSON.stringify(JSON.parse(content), null, 2);
    } catch {
      return content;
    }
  }

  async clearAll(): Promise<void> {
    if (!window.confirm('Delete every recorded API test call? This cannot be undone.')) {
      return;
    }

    try {
      await firstValueFrom(this.http.delete(`${BACKEND_BASE_URL}/api/apitest/calls`));
      await this.refresh();
    } catch {
      this.loadError.set('Could not clear calls — is the backend running on ' + BACKEND_BASE_URL + '?');
    }
  }

  async copyToClipboard(text: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(text);
    } catch {
      // Clipboard API can be unavailable (insecure context, permissions) — non-fatal, the text is still
      // visible on screen for a manual copy.
    }
  }
}
