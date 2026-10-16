import { of } from 'rxjs';
import { AuthStore } from '../../auth/store/auth.store';
import { makeTestUser } from '../../auth/testing/auth-test-helpers';
import { CdsApiService } from '../services/cds-api.service';
import {
  CdsCallLog,
  CdsDiscoveryInfo,
  CdsFixTypeDescriptor,
  CdsHandler,
  CdsSetupSheet,
  CdsServiceInstance,
  CdsSettingDescriptor,
  CdsTestResult,
  CdsTrustedIssuer,
} from '../models/cds.models';

// Shared builders for the Clinical Decision Support specs. Values are made up; nothing here is patient data.

export const CDS_ALL_PERMISSIONS = ['cdshooks.view', 'cdshooks.create', 'cdshooks.edit', 'cdshooks.delete', 'cdshooks.test'];

/** Signs in a non-admin test user holding exactly these permission codes. */
export function signInWith(store: AuthStore, permissions: string[]): void {
  store.setUser(makeTestUser(permissions));
}

export function makeSetting(overrides: Partial<CdsSettingDescriptor> = {}): CdsSettingDescriptor {
  return {
    key: 'threshold',
    label: 'Threshold',
    type: 'number',
    defaultValue: null,
    help: null,
    options: null,
    required: false,
    min: null,
    max: null,
    ...overrides,
  };
}

/** A "remove the draft order" fix: no code needed. */
export function makeDeleteFixType(overrides: Partial<CdsFixTypeDescriptor> = {}): CdsFixTypeDescriptor {
  return {
    key: 'remove-draft',
    label: 'Remove the draft order',
    actionType: 'delete',
    resourceType: 'MedicationRequest',
    mappingFields: [],
    requiresMapping: false,
    triggers: null,
    codeSystems: null,
    help: null,
    ...overrides,
  };
}

/** An "add an alternative drug" fix: needs a code mapping, keyed by drug class, display required. */
export function makeCreateFixType(overrides: Partial<CdsFixTypeDescriptor> = {}): CdsFixTypeDescriptor {
  return {
    key: 'add-alternative',
    label: 'Order an alternative',
    actionType: 'create',
    resourceType: 'MedicationRequest',
    mappingFields: [
      { key: 'trigger', label: 'Drug class', required: false, help: null },
      { key: 'system', label: 'Code system', required: true, help: null },
      { key: 'code', label: 'Code', required: true, help: null },
      { key: 'display', label: 'Name', required: true, help: 'Shown on the button.' },
    ],
    requiresMapping: true,
    triggers: [{ value: 'nsaid', label: 'NSAIDs' }, { value: 'opioid', label: 'Opioids' }],
    codeSystems: [
      { value: 'urn:com.epic.cdshooks.action.code.system.preference-list-item', label: 'Epic preference list item' },
      { value: 'urn:oid:2.16.840.1.113883.6.88', label: 'RxNorm' },
    ],
    help: null,
    ...overrides,
  };
}

export function makeHandler(overrides: Partial<CdsHandler> = {}): CdsHandler {
  return {
    key: 'renal-dose',
    displayName: 'Renal dose check',
    description: 'Warns when a drug needs a lower dose for low eGFR.',
    supportedHooks: ['order-select', 'order-sign'],
    settings: [makeSetting({ key: 'egfrThreshold', label: 'eGFR threshold', defaultValue: 30, min: 5, max: 120, required: true })],
    prefetchByHook: {
      'order-select': { egfr: 'Observation?patient={{context.patientId}}&code=33914-3' },
      'order-sign': { egfr: 'Observation?patient={{context.patientId}}&code=33914-3' },
    },
    fixTypes: [],
    ...overrides,
  };
}

export function makeService(overrides: Partial<CdsServiceInstance> = {}): CdsServiceInstance {
  return {
    id: 'svc-1',
    serviceId: 'renal-dose-check',
    title: 'Renal dose check',
    handlerKey: 'renal-dose',
    handlerDisplayName: 'Renal dose check',
    hook: 'order-sign',
    description: '',
    isEnabled: true,
    settings: { egfrThreshold: 30 },
    storedSettings: {},
    cardSourceLabel: 'Pharmacy',
    cardSourceUrl: null,
    allowedIssuerIds: [],
    prefetch: { egfr: 'Observation?code=33914-3' },
    createdOnUtc: '2026-10-01T10:00:00Z',
    createdBy: 'admin@test.com',
    modifiedOnUtc: null,
    modifiedBy: null,
    fetchMissingData: true,
    fixes: { enabled: false, fixTypes: [] },
    fixesEnabled: false,
    overrideReasons: [],
    overrideReasonsAreDefault: true,
    links: [],
    supportedFixTypes: [],
    ...overrides,
  };
}

export function makeIssuer(overrides: Partial<CdsTrustedIssuer> = {}): CdsTrustedIssuer {
  return {
    id: 'iss-1',
    name: 'Main hospital Epic',
    vendor: 'Epic',
    issuer: 'https://ehr.example.org',
    jwksUrl: 'https://ehr.example.org/jwks.json',
    jwksJson: null,
    audience: null,
    isEnabled: true,
    createdOnUtc: '2026-10-01T10:00:00Z',
    createdBy: null,
    modifiedOnUtc: null,
    modifiedBy: null,
    expectedSubject: null,
    fhirBaseUrl: null,
    effectiveFhirBaseUrl: 'https://ehr.example.org',
    allowCallback: true,
    ...overrides,
  };
}

export function makeDiscovery(overrides: Partial<CdsDiscoveryInfo> = {}): CdsDiscoveryInfo {
  return {
    publicBaseUrl: 'https://segue.example.org',
    isConfigured: true,
    configurationKey: 'Cds:PublicBaseUrl',
    discoveryUrl: 'https://segue.example.org/cds-services',
    services: [],
    ...overrides,
  };
}

export function makeTestResult(overrides: Partial<CdsTestResult> = {}): CdsTestResult {
  return {
    outcome: 'Success',
    httpStatus: 200,
    durationMs: 42,
    messages: [],
    resolvedSettings: { egfrThreshold: 30 },
    response: {
      cards: [
        {
          uuid: 'c1',
          summary: 'Reduce the dose: eGFR is below 30',
          detail: '**Sample** rule. Requires clinical review.',
          indicator: 'warning',
          source: { label: 'Pharmacy', url: 'https://pharmacy.example.org' },
          links: [{ label: 'Dosing guide', url: 'https://guide.example.org', type: 'absolute' }],
        },
      ],
    },
    ...overrides,
  };
}

export function makeCallLog(overrides: Partial<CdsCallLog> = {}): CdsCallLog {
  return {
    id: 'l1', occurredOnUtc: '2026-10-10T12:00:00Z', kind: 'Invoke', serviceId: 'renal-dose-check',
    serviceInstanceId: 'svc-1', serviceTitle: 'Renal dose check', handlerKey: 'renal-dose', hook: 'order-sign',
    hookInstance: 'h-1', issuer: 'https://ehr.example.org', trustedIssuerId: 'iss-1', trustedIssuerName: 'Main hospital Epic',
    outcome: 'Success', httpStatus: 200, cardCount: 1, durationMs: 50, detail: null,
    ...overrides,
  };
}

export function makeSetupSheet(overrides: Partial<CdsSetupSheet> = {}): CdsSetupSheet {
  return {
    serviceInstanceId: 'svc-1',
    serviceId: 'renal-dose-check',
    title: 'Renal dose check',
    hook: 'order-sign',
    isEnabled: true,
    serviceUrl: 'https://segue.example.org/cds-services/renal-dose-check',
    feedbackUrl: 'https://segue.example.org/cds-services/renal-dose-check/feedback',
    defaultAudience: 'https://segue.example.org/cds-services/renal-dose-check',
    publicBaseUrlConfigured: true,
    fetchMissingData: true,
    fixesEnabled: false,
    prefetch: [{ key: 'egfr', query: 'Observation?patient={{context.patientId}}&code=33914-3', fetchedIfMissing: true }],
    overrideReasons: [{ code: 'will-monitor', display: 'Will monitor the patient' }],
    overrideReasonSystem: 'https://segue.example.org/cds-services/override-reasons',
    fixes: [],
    links: [],
    issuers: [{
      id: 'iss-1', name: 'Main hospital Epic', vendor: 'Epic', issuer: 'https://ehr.example.org/api/FHIR/R4',
      jwksUrl: 'https://ehr.example.org/jwks', hasPastedJwks: false, expectedSubject: 'client-123',
      expectedAudience: 'https://segue.example.org/cds-services/renal-dose-check',
      effectiveFhirBaseUrl: 'https://ehr.example.org/api/FHIR/R4', allowCallback: true, isEnabled: true,
    }],
    warnings: [],
    generatedOnUtc: '2026-10-10T12:00:00Z',
    ...overrides,
  };
}

/** A CdsApiService spy whose reads answer with empty data unless a spec says otherwise. */
export function makeCdsApiSpy(): jasmine.SpyObj<CdsApiService> {
  const api = jasmine.createSpyObj<CdsApiService>('CdsApiService', [
    'getServices', 'getService', 'createService', 'updateService', 'deleteService', 'getSampleRequest', 'testService',
    'getTrustedIssuers', 'createTrustedIssuer', 'updateTrustedIssuer', 'deleteTrustedIssuer',
    'getHandlers', 'getDiscoveryInfo', 'getActivity', 'getSetupSheet', 'testCallback',
  ]);
  api.getServices.and.returnValue(of([]));
  api.getTrustedIssuers.and.returnValue(of([]));
  api.getHandlers.and.returnValue(of([]));
  api.getDiscoveryInfo.and.returnValue(of(makeDiscovery()));
  api.getActivity.and.returnValue(of({ items: [], totalCount: 0, page: 1, pageSize: 25 }));
  return api;
}

/** A ToastService stand-in that records what was shown. */
export function makeToastSpy(): { success: jasmine.Spy; error: jasmine.Spy; warning: jasmine.Spy; info: jasmine.Spy } {
  return {
    success: jasmine.createSpy('success'),
    error: jasmine.createSpy('error'),
    warning: jasmine.createSpy('warning'),
    info: jasmine.createSpy('info'),
  };
}

/** A DialogService stand-in: every open() closes straight away with `result`. */
export function makeDialogSpy(result: unknown = undefined): { open: jasmine.Spy } {
  return { open: jasmine.createSpy('open').and.callFake(() => ({ afterClosed: () => of(result) })) };
}
