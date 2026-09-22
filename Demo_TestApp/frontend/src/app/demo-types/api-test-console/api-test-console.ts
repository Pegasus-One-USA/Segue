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
  content: string;
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

/**
 * A small, self-contained console for exercising FHIRBridge's ApiEndpoint destination end to end: four sample
 * APIs (see SAMPLE_APIS above / backend/ApiEndpointTestEndpoints.cs), each expecting a different request shape,
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

  protected readonly activeTab = signal<'calls' | 'docs'>('calls');
  protected readonly calls = signal<ApiTestCall[]>([]);
  protected readonly loading = signal(false);
  protected readonly loadError = signal('');
  protected readonly apiNameFilter = signal('');
  protected readonly expandedCallId = signal<number | null>(null);

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
