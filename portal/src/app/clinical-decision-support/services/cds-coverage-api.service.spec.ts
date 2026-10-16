import { TestBed } from '@angular/core/testing';
import { HttpHeaders, HttpResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CdsCoverageApiService, cdsFileNameFrom } from './cds-coverage-api.service';
import { CDS_ENDPOINTS } from '../../core/api-endpoints';
import { SaveCdsCoverageRuleRequest, SaveCdsPayerRequest } from '../models/cds-coverage.models';
import { makeCoverageRule, makeImportResult, makePayer, makeRelayTestResult } from '../testing/cds-test-data';

describe('CdsCoverageApiService', () => {
  let api: CdsCoverageApiService;
  let http: HttpTestingController;

  const SAVE_PAYER: SaveCdsPayerRequest = {
    name: 'Acme', identifiers: ['ACME01'], planValues: [], isEnabled: true, relayEnabled: false, relayServiceUrl: null,
    relayTimeoutMs: 1000, fallbackToLocalRules: true, forwardFhirAuthorization: false, relayClientId: null,
  };
  const SAVE_RULE: SaveCdsCoverageRuleRequest = {
    payerId: 'payer-1', system: 'http://www.ama-assn.org/go/cpt', code: '70553', codeMatch: 'exact', covered: 'covered',
    paNeeded: 'auth-needed', docNeeded: null, docPurposes: [], infoNeeded: [], reason: null, policyUrl: null,
    questionnaire: null, effectiveFrom: null, effectiveTo: null, isEnabled: true,
  };

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(CdsCoverageApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads, creates, updates and deletes payers', () => {
    api.getPayers().subscribe(list => expect(list.length).toBe(1));
    http.expectOne({ method: 'GET', url: CDS_ENDPOINTS.payers }).flush([makePayer()]);

    api.createPayer(SAVE_PAYER).subscribe();
    const create = http.expectOne({ method: 'POST', url: CDS_ENDPOINTS.payers });
    expect(create.request.body).toEqual(SAVE_PAYER);
    create.flush(makePayer());

    api.updatePayer('payer-1', SAVE_PAYER).subscribe();
    http.expectOne({ method: 'PUT', url: CDS_ENDPOINTS.payerById('payer-1') }).flush(makePayer());

    api.deletePayer('payer-1').subscribe();
    http.expectOne({ method: 'DELETE', url: CDS_ENDPOINTS.payerById('payer-1') }).flush(null);
  });

  it('tests a payer relay', () => {
    api.testRelay('payer-1').subscribe(r => expect(r.reachable).toBeTrue());
    http.expectOne({ method: 'POST', url: CDS_ENDPOINTS.testRelay('payer-1') }).flush(makeRelayTestResult());
  });

  it('pages rules with only the filters that are set', () => {
    api.getRules({ page: 2, pageSize: 50, payerId: 'payer-1', enabled: false }).subscribe(p => expect(p.totalCount).toBe(1));
    const req = http.expectOne(r => r.url === CDS_ENDPOINTS.coverageRules);
    expect(req.request.params.get('page')).toBe('2');
    expect(req.request.params.get('pageSize')).toBe('50');
    expect(req.request.params.get('payerId')).toBe('payer-1');
    expect(req.request.params.get('enabled')).toBe('false');
    expect(req.request.params.has('code')).toBeFalse();
    expect(req.request.params.has('search')).toBeFalse();
    req.flush({ items: [makeCoverageRule()], totalCount: 1, page: 2, pageSize: 50 });
  });

  it('creates, updates and deletes rules', () => {
    api.createRule(SAVE_RULE).subscribe();
    expect(http.expectOne({ method: 'POST', url: CDS_ENDPOINTS.coverageRules }).request.body).toEqual(SAVE_RULE);
    api.updateRule('rule-1', SAVE_RULE).subscribe();
    http.expectOne({ method: 'PUT', url: CDS_ENDPOINTS.coverageRuleById('rule-1') }).flush(makeCoverageRule());
    api.deleteRule('rule-1').subscribe();
    http.expectOne({ method: 'DELETE', url: CDS_ENDPOINTS.coverageRuleById('rule-1') }).flush(null);
  });

  it('sends an import as the multipart field "file" with format, mode, strategy and payer', () => {
    const file = new File(['payer,system,code,covered\n'], 'rules.csv', { type: 'text/csv' });
    api.importRules({ file, format: 'csv', mode: 'preview', strategy: 'replace', payerId: 'payer-1' })
      .subscribe(r => expect(r.added).toBe(1));
    const req = http.expectOne(r => r.url === CDS_ENDPOINTS.coverageRulesImport);
    expect(req.request.method).toBe('POST');
    expect(req.request.params.get('format')).toBe('csv');
    expect(req.request.params.get('mode')).toBe('preview');
    expect(req.request.params.get('strategy')).toBe('replace');
    expect(req.request.params.get('payerId')).toBe('payer-1');
    expect((req.request.body as FormData).get('file')).toEqual(jasmine.any(File));
    req.flush(makeImportResult());
  });

  it('leaves the payer out of an import for every payer', () => {
    const file = new File(['x'], 'rules.json');
    api.importRules({ file, format: 'json', mode: 'apply', strategy: 'merge', payerId: null }).subscribe();
    const req = http.expectOne(r => r.url === CDS_ENDPOINTS.coverageRulesImport);
    expect(req.request.params.has('payerId')).toBeFalse();
    req.flush(makeImportResult({ mode: 'apply', applied: true }));
  });

  it('downloads an export and a template with the server file name', () => {
    api.exportRules('json', 'payer-1').subscribe(f => {
      expect(f.fileName).toBe('segue-coverage-rules-20261016.json');
      expect(f.blob.size).toBeGreaterThan(0);
    });
    const exportReq = http.expectOne(r => r.url === CDS_ENDPOINTS.coverageRulesExport);
    expect(exportReq.request.params.get('format')).toBe('json');
    expect(exportReq.request.params.get('payerId')).toBe('payer-1');
    exportReq.flush(new Blob(['[]']), { headers: { 'Content-Disposition': 'attachment; filename=segue-coverage-rules-20261016.json' } });

    api.getImportTemplate('csv').subscribe(f => expect(f.fileName).toBe('segue-coverage-rules-template.csv'));
    const templateReq = http.expectOne(r => r.url === CDS_ENDPOINTS.coverageRulesTemplate);
    expect(templateReq.request.params.get('format')).toBe('csv');
    templateReq.flush(new Blob(['payer']));
  });

  it('turns a blob error body into the API reason', async () => {
    const reason = new Promise<string>(resolve => {
      api.exportRules('csv', 'gone').subscribe({ error: (err: { message: string }) => resolve(err.message) });
    });
    http.expectOne(r => r.url === CDS_ENDPOINTS.coverageRulesExport).flush(
      new Blob([JSON.stringify({ error: 'The payer no longer exists.' })], { type: 'application/json' }),
      { status: 404, statusText: 'Not Found' });

    expect(await reason).toBe('The payer no longer exists.');
  });

  it('reads quoted and UTF-8 file names', () => {
    const response = (header: string) => new HttpResponse<Blob>({ headers: new HttpHeaders({ 'Content-Disposition': header }) });
    expect(cdsFileNameFrom(response('attachment; filename="a b.csv"'), 'x')).toBe('a b.csv');
    expect(cdsFileNameFrom(response("attachment; filename=a.csv; filename*=UTF-8''r%C3%A8gles.csv"), 'x')).toBe('règles.csv');
    expect(cdsFileNameFrom(new HttpResponse<Blob>({}), 'fallback.csv')).toBe('fallback.csv');
  });
});
