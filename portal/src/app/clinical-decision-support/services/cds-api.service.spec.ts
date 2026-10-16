import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CdsApiService } from './cds-api.service';
import { CDS_ENDPOINTS } from '../../core/api-endpoints';
import { CdsRequest, SaveCdsServiceInstanceRequest, SaveCdsTrustedIssuerRequest } from '../models/cds.models';
import { makeIssuer, makeService } from '../testing/cds-test-data';

describe('CdsApiService', () => {
  let api: CdsApiService;
  let http: HttpTestingController;

  const SAVE_SERVICE: SaveCdsServiceInstanceRequest = {
    serviceId: 'renal-dose-check', title: 'Renal', handlerKey: 'renal-dose', hook: 'order-sign', description: null,
    isEnabled: true, settings: { egfrThreshold: 30 }, cardSourceLabel: null, cardSourceUrl: null, allowedIssuerIds: [],
    fetchMissingData: true, fixes: null, overrideReasons: null, links: null,
  };
  const SAVE_ISSUER: SaveCdsTrustedIssuerRequest = {
    name: 'Sandbox', vendor: 'Other', issuer: 'https://sandbox.cds-hooks.org',
    jwksUrl: 'https://sandbox.cds-hooks.org/.well-known/jwks.json', jwksJson: null, audience: null, isEnabled: true,
    expectedSubject: null, fhirBaseUrl: null, allowCallback: true,
  };

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(CdsApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads services, one service and its sample request', () => {
    api.getServices().subscribe(list => expect(list.length).toBe(1));
    http.expectOne({ method: 'GET', url: CDS_ENDPOINTS.services }).flush([makeService()]);

    api.getService('svc-1').subscribe(s => expect(s.id).toBe('svc-1'));
    http.expectOne({ method: 'GET', url: CDS_ENDPOINTS.serviceById('svc-1') }).flush(makeService());

    api.getSampleRequest('svc-1').subscribe(r => expect(r.hook).toBe('order-sign'));
    http.expectOne({ method: 'GET', url: CDS_ENDPOINTS.sampleRequest('svc-1') })
      .flush({ hook: 'order-sign', hookInstance: 'h1', context: {} });
  });

  it('creates, updates and deletes a service with the request body unchanged', () => {
    api.createService(SAVE_SERVICE).subscribe();
    const create = http.expectOne({ method: 'POST', url: CDS_ENDPOINTS.services });
    expect(create.request.body).toEqual(SAVE_SERVICE);
    create.flush(makeService());

    api.updateService('svc-1', SAVE_SERVICE).subscribe();
    const update = http.expectOne({ method: 'PUT', url: CDS_ENDPOINTS.serviceById('svc-1') });
    expect(update.request.body).toEqual(SAVE_SERVICE);
    update.flush(makeService());

    api.deleteService('svc-1').subscribe();
    http.expectOne({ method: 'DELETE', url: CDS_ENDPOINTS.serviceById('svc-1') }).flush(null);
  });

  it('posts a test request to the test endpoint', () => {
    const request: CdsRequest = { hook: 'order-sign', hookInstance: 'h1', context: { userId: 'Practitioner/1' } };
    api.testService('svc-1', request).subscribe(r => expect(r.outcome).toBe('NoCards'));
    const req = http.expectOne({ method: 'POST', url: CDS_ENDPOINTS.test('svc-1') });
    expect(req.request.body).toEqual(request);
    req.flush({ outcome: 'NoCards', httpStatus: 200, response: { cards: [] }, durationMs: 3, messages: [], resolvedSettings: {} });
  });

  it('reads and writes trusted issuers', () => {
    api.getTrustedIssuers().subscribe();
    http.expectOne({ method: 'GET', url: CDS_ENDPOINTS.trustedIssuers }).flush([makeIssuer()]);

    api.createTrustedIssuer(SAVE_ISSUER).subscribe();
    const create = http.expectOne({ method: 'POST', url: CDS_ENDPOINTS.trustedIssuers });
    expect(create.request.body).toEqual(SAVE_ISSUER);
    create.flush(makeIssuer());

    api.updateTrustedIssuer('iss-1', SAVE_ISSUER).subscribe();
    http.expectOne({ method: 'PUT', url: CDS_ENDPOINTS.trustedIssuerById('iss-1') }).flush(makeIssuer());

    api.deleteTrustedIssuer('iss-1').subscribe();
    http.expectOne({ method: 'DELETE', url: CDS_ENDPOINTS.trustedIssuerById('iss-1') }).flush(null);
  });

  it('reads the handler catalog and the discovery info', () => {
    api.getHandlers().subscribe(list => expect(list).toEqual([]));
    http.expectOne({ method: 'GET', url: CDS_ENDPOINTS.handlers }).flush([]);

    api.getDiscoveryInfo().subscribe(info => expect(info.isConfigured).toBeFalse());
    http.expectOne({ method: 'GET', url: CDS_ENDPOINTS.discoveryInfo }).flush({ isConfigured: false });
  });

  it('reads a service setup sheet and posts a callback test with no body', () => {
    api.getSetupSheet('svc-1').subscribe(s => expect(s.serviceId).toBe('renal-dose-check'));
    http.expectOne({ method: 'GET', url: CDS_ENDPOINTS.setupSheet('svc-1') }).flush({ serviceId: 'renal-dose-check' });

    api.testCallback('iss-1').subscribe(r => expect(r.reachable).toBeTrue());
    const req = http.expectOne({ method: 'POST', url: CDS_ENDPOINTS.testCallback('iss-1') });
    expect(req.request.body).toBeNull();
    req.flush({ reachable: true });
  });

  it('sends only the paging params when no activity filter is set', () => {
    api.getActivity({ page: 2, pageSize: 50 }).subscribe();
    const req = http.expectOne(r => r.url === CDS_ENDPOINTS.activity);
    expect(req.request.params.keys().sort()).toEqual(['page', 'pageSize']);
    expect(req.request.params.get('page')).toBe('2');
    expect(req.request.params.get('pageSize')).toBe('50');
    req.flush({ items: [], totalCount: 0, page: 2, pageSize: 50 });
  });

  it('sends every activity filter that is set', () => {
    api.getActivity({
      page: 1, pageSize: 25, serviceId: 'renal-dose-check', kind: 'Feedback', outcome: 'Degraded',
      fromUtc: '2026-10-01T00:00:00.000Z', toUtc: '2026-10-02T00:00:00.000Z',
    }).subscribe();
    const req = http.expectOne(r => r.url === CDS_ENDPOINTS.activity);
    const p = req.request.params;
    expect(p.get('serviceId')).toBe('renal-dose-check');
    expect(p.get('kind')).toBe('Feedback');
    expect(p.get('outcome')).toBe('Degraded');
    expect(p.get('fromUtc')).toBe('2026-10-01T00:00:00.000Z');
    expect(p.get('toUtc')).toBe('2026-10-02T00:00:00.000Z');
    req.flush({ items: [], totalCount: 0, page: 1, pageSize: 25 });
  });
});
