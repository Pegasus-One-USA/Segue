import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { WizardService, WizardFormValues } from './wizard.service';
import { ISourceConnectionService } from '../source-connections/services/i-source-connection.service';
import { SourceConnectionModel, SourceConnectionRequest } from '../source-connections/models/source-connection.model';

/**
 * WizardService is a root singleton shared by Source Connections (read) and Destination Connections' EHR write
 * connections (write). The purpose must never leak from one open to the next, and only a write-purpose save may send
 * Access: a source save sends null so it can never downgrade a Write / Read & Write connection.
 */
describe('WizardService — purpose', () => {
  let wiz: WizardService;
  let create: jasmine.Spy;
  let update: jasmine.Spy;

  const formValues: WizardFormValues = {
    stepName: 'Conn', baseUrl: 'https://ehr.example/fhir', token: 'https://ehr.example/token', authorize: '',
    algorithm: 'RS384', jwksMethod: 'hosted', jwksUrl: '', kid: '', kvRef: '', redirectUri: '', launchUrl: '',
  };
  const backendFields = (extra: Record<string, string> = {}): Record<string, string> => ({
    'Epic audience': 'backend-system', 'Auth method': 'secret', 'Client ID': 'abc', ...extra,
  });
  const sent = (spy: jasmine.Spy): SourceConnectionRequest => spy.calls.mostRecent().args.at(-1) as SourceConnectionRequest;

  beforeEach(() => {
    create = jasmine.createSpy('create').and.returnValue(of({}));
    update = jasmine.createSpy('update').and.returnValue(of({}));
    TestBed.configureTestingModule({
      providers: [{ provide: ISourceConnectionService, useValue: { create, update } }],
    });
    wiz = TestBed.inject(WizardService);
  });

  afterEach(() => wiz.close());

  it('opens in write purpose with the Backend System audience, and close() resets it to read', () => {
    wiz.openEntity(null, { purpose: 'write' });
    expect(wiz.purpose()).toBe('write');
    expect(wiz.epicAudience()).toBe('backend-system');

    wiz.close();
    expect(wiz.purpose()).toBe('read');
  });

  it('defaults to read, and a canvas open() resets a leftover write purpose', () => {
    wiz.openEntity(null);
    expect(wiz.purpose()).toBe('read');
    expect(wiz.epicAudience()).toBe('provider-ehr-launch');

    wiz.openEntity(null, { purpose: 'write' });
    wiz.open();
    expect(wiz.purpose()).toBe('read');
  });

  it('a read-purpose save sends no Access, activation or department, even when the fields carry them', () => {
    wiz.openEntity({ id: 'c1', name: 'Conn', sourceSystemType: 'Epic', baseUrl: 'https://x', isEnabled: true,
      authentication: { authenticationType: 'OAuthClientCredentials', scopes: [] }, access: 'ReadWrite' } as SourceConnectionModel);

    wiz.save(formValues, backendFields({ Access: 'Read', 'Vendor write APIs activated': 'false', 'Department ID': '' }));

    const request = sent(update);
    expect(request.access).toBeNull();
    expect(request.vendorWriteApisActivated).toBeNull();
    expect(request.departmentId).toBeNull();
  });

  it('a new write connection is created Write with its activation and department', () => {
    wiz.openEntity(null, { purpose: 'write' });
    wiz.ehrType.set('Athenahealth');

    wiz.save(formValues, backendFields({ Access: 'Write', 'Vendor write APIs activated': 'true', 'Department ID': ' 150 ' }));

    const request = sent(create);
    expect(request.access).toBe('Write');
    expect(request.vendorWriteApisActivated).toBeTrue();
    expect(request.departmentId).toBe('150');
  });

  it('publishes the saved connection before bumping saved, so a caller can pick it at once', () => {
    const created = { id: 'w1', name: 'athena write', sourceSystemType: 'Athenahealth' } as SourceConnectionModel;
    create.and.returnValue(of(created));
    const before = wiz.saved();
    wiz.openEntity(null, { purpose: 'write' });
    wiz.ehrType.set('Athenahealth');

    wiz.save(formValues, backendFields({ Access: 'Write' }));

    expect(wiz.lastSavedEntity()).toBe(created);
    expect(wiz.saved()).toBe(before + 1);
  });

  it('editing a write connection keeps its saved access (null) and can clear the department', () => {
    wiz.openEntity({ id: 'w1', name: 'Conn', sourceSystemType: 'Athenahealth', baseUrl: 'https://x', isEnabled: true,
      authentication: { authenticationType: 'OAuthClientCredentials', scopes: [] }, access: 'ReadWrite',
      applicationType: 'Backend', departmentId: '150' } as SourceConnectionModel, { purpose: 'write' });

    wiz.save(formValues, backendFields({ Access: 'ReadWrite', 'Department ID': '' }));

    const request = sent(update);
    expect(request.access).toBeNull();
    expect(request.departmentId).toBe('');
  });
  it('editing a Read & Write connection in write purpose keeps its saved retrieval and scopes', () => {
    const retrieval = { retrievalMethod: 'search-rest', resourceTypes: ['Patient', 'Encounter'], searchCriteria: '_lastUpdated=gt2026-01-01', incrementalSyncEnabled: true };
    wiz.openEntity({ id: 'rw1', name: 'Conn', sourceSystemType: 'Epic', baseUrl: 'https://x', isEnabled: true,
      authentication: { authenticationType: 'OAuthClientCredentials', scopes: ['system/Patient.read', 'system/Encounter.read'] },
      access: 'ReadWrite', applicationType: 'Backend', retrieval } as SourceConnectionModel, { purpose: 'write' });

    wiz.save(formValues, backendFields({ 'Vendor write APIs activated': 'true' }));

    const request = sent(update);
    expect(request.retrieval).toEqual(retrieval);
    expect(request.authentication.scopes).toEqual(['system/Patient.read', 'system/Encounter.read']);
  });
});
