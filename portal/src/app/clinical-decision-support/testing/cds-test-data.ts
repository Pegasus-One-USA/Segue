import { of } from 'rxjs';
import { AuthStore } from '../../auth/store/auth.store';
import { makeTestUser } from '../../auth/testing/auth-test-helpers';
import { CdsApiService } from '../services/cds-api.service';
import { CdsCoverageApiService } from '../services/cds-coverage-api.service';
import {
  CdsCallLog,
  CdsDiscoveryInfo,
  CdsFixTypeDescriptor,
  CdsHandler,
  CdsReadiness,
  CdsReadinessCheck,
  CdsSetupSheet,
  CdsSetupSheetCrd,
  CdsServiceInstance,
  CdsSettingDescriptor,
  CdsTestResult,
  CdsTrustedIssuer,
} from '../models/cds.models';
import {
  CdsCoverageImportResult,
  CdsCoverageRule,
  CdsPayer,
  CdsPayerRelayTestResult,
} from '../models/cds-coverage.models';

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
    isClinicallySignedOff: false,
    signedOffBy: null,
    signedOffAtUtc: null,
    signOffNote: null,
    usesSampleReferenceData: true,
    contentVersion: 'v1',
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
    isClinicallySignedOff: false,
    signedOffBy: null,
    signedOffAtUtc: null,
    signOffNote: null,
    ...overrides,
  };
}

export function makeSetupSheetCrd(overrides: Partial<CdsSetupSheetCrd> = {}): CdsSetupSheetCrd {
  return {
    hook: 'order-sign',
    epicApis: ['Coverage.Read', 'ServiceRequest.Update (Unsigned Order)'],
    useSystemActions: true,
    dtrAppUrl: null,
    dtrLinkType: 'smart',
    segueJwksUrl: 'https://segue.example.org/cds-services/.well-known/jwks.json',
    payers: [
      {
        id: 'payer-1', name: 'Acme Health Plan', isEnabled: true, identifiers: ['ACME01'], planValues: [], enabledRuleCount: 2,
        relayEnabled: false, relayServiceUrl: null, forwardFhirAuthorization: false,
      },
      {
        id: 'payer-2', name: 'Beta Health', isEnabled: false, identifiers: ['BETA'], planValues: ['GOLD'], enabledRuleCount: 0,
        relayEnabled: true, relayServiceUrl: 'https://crd.beta.example/svc', forwardFhirAuthorization: true,
      },
    ],
    notes: ["Configure the hook in Epic's CRD Request outgoing call."],
    ...overrides,
  };
}

export function makeReadinessCheck(overrides: Partial<CdsReadinessCheck> = {}): CdsReadinessCheck {
  return {
    key: 'public-base-url',
    label: 'Public address (Cds:PublicBaseUrl)',
    status: 'ok',
    detail: 'Services are published under https://segue.example.org/cds-services.',
    howToFix: null,
    ...overrides,
  };
}

export function makeReadiness(overrides: Partial<CdsReadiness> = {}): CdsReadiness {
  return {
    status: 'ok',
    checks: [makeReadinessCheck()],
    generatedOnUtc: '2026-10-16T09:00:00Z',
    ...overrides,
  };
}

/** A CdsApiService spy whose reads answer with empty data unless a spec says otherwise. */
export function makeCdsApiSpy(): jasmine.SpyObj<CdsApiService> {
  const api = jasmine.createSpyObj<CdsApiService>('CdsApiService', [
    'getServices', 'getService', 'createService', 'updateService', 'deleteService', 'getSampleRequest', 'testService',
    'getTrustedIssuers', 'createTrustedIssuer', 'updateTrustedIssuer', 'deleteTrustedIssuer',
    'getHandlers', 'getDiscoveryInfo', 'getActivity', 'getSetupSheet', 'testCallback',
    'signOffService', 'clearSignOff', 'getReadiness',
  ]);
  api.getServices.and.returnValue(of([]));
  api.getTrustedIssuers.and.returnValue(of([]));
  api.getHandlers.and.returnValue(of([]));
  api.getDiscoveryInfo.and.returnValue(of(makeDiscovery()));
  api.getActivity.and.returnValue(of({ items: [], totalCount: 0, page: 1, pageSize: 25 }));
  api.getReadiness.and.returnValue(of(makeReadiness()));
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

// ─── Coverage requirements (CRD) ────────────────────────────────────────────────

export function makePayer(overrides: Partial<CdsPayer> = {}): CdsPayer {
  return {
    id: 'payer-1',
    name: 'Acme Health Plan',
    identifiers: ['ACME01'],
    planValues: [],
    isEnabled: true,
    relayEnabled: false,
    relayServiceUrl: null,
    relayTimeoutMs: 1000,
    fallbackToLocalRules: true,
    forwardFhirAuthorization: false,
    relayClientId: null,
    ruleCount: 2,
    enabledRuleCount: 2,
    createdOnUtc: '2026-10-01T10:00:00Z',
    createdBy: 'admin@test.com',
    modifiedOnUtc: null,
    modifiedBy: null,
    ...overrides,
  };
}

export function makeCoverageRule(overrides: Partial<CdsCoverageRule> = {}): CdsCoverageRule {
  return {
    id: 'rule-1',
    payerId: 'payer-1',
    payerName: 'Acme Health Plan',
    system: 'http://www.ama-assn.org/go/cpt',
    code: '70551',
    codeMatch: 'exact',
    covered: 'covered',
    paNeeded: 'auth-needed',
    docNeeded: 'clinical',
    docPurposes: ['withpa'],
    infoNeeded: [],
    reason: 'MRI of the brain needs prior authorization.',
    policyUrl: 'https://payer.example.org/policy/mri',
    questionnaire: null,
    effectiveFrom: '2000-01-01',
    effectiveTo: null,
    isEnabled: true,
    isEffectiveToday: true,
    createdOnUtc: '2026-10-01T10:00:00Z',
    createdBy: 'admin@test.com',
    modifiedOnUtc: null,
    modifiedBy: null,
    ...overrides,
  };
}

export function makeRelayTestResult(overrides: Partial<CdsPayerRelayTestResult> = {}): CdsPayerRelayTestResult {
  return {
    payerId: 'payer-1',
    relayServiceUrl: 'https://crd.payer.example.org/cds-services/crd',
    reachable: true,
    httpStatus: 200,
    durationMs: 180,
    cardCount: 1,
    systemActionCount: 1,
    outcome: 'ok',
    message: 'The payer answered with a valid CDS Hooks response.',
    ...overrides,
  };
}

export function makeImportResult(overrides: Partial<CdsCoverageImportResult> = {}): CdsCoverageImportResult {
  return {
    mode: 'preview',
    format: 'csv',
    strategy: 'merge',
    applied: false,
    totalRows: 2,
    validRows: 2,
    added: 1,
    updated: 1,
    unchanged: 0,
    deleted: 0,
    errorCount: 0,
    fileErrors: [],
    rows: [
      { rowNumber: 2, payer: 'Acme Health Plan', system: 'http://www.ama-assn.org/go/cpt', code: '70551', effectiveFrom: '2000-01-01', action: 'add', errors: [] },
      { rowNumber: 3, payer: 'Acme Health Plan', system: 'http://www.ama-assn.org/go/cpt', code: '70553', effectiveFrom: '2000-01-01', action: 'update', errors: [] },
    ],
    ...overrides,
  };
}

/** A CdsCoverageApiService spy whose reads answer with empty data unless a spec says otherwise. */
export function makeCoverageApiSpy(): jasmine.SpyObj<CdsCoverageApiService> {
  const api = jasmine.createSpyObj<CdsCoverageApiService>('CdsCoverageApiService', [
    'getPayers', 'createPayer', 'updatePayer', 'deletePayer', 'testRelay',
    'getRules', 'createRule', 'updateRule', 'deleteRule',
    'importRules', 'exportRules', 'getImportTemplate',
  ]);
  api.getPayers.and.returnValue(of([]));
  api.getRules.and.returnValue(of({ items: [], totalCount: 0, page: 1, pageSize: 25 }));
  return api;
}
