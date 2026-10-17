import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { GenericFhirWriteConnectionFormComponent } from './generic-fhir-write-connection-form.component';
import { ISourceConnectionService } from '../../../source-connections/services/i-source-connection.service';
import { SourceConnectionModel, SourceConnectionRequest } from '../../../source-connections/models/source-connection.model';
import { ToastService } from '../../../services/toast.service';

/**
 * The FHIR server write connection form: a new one is a write-only, unauthenticated Backend connection; an edit
 * changes only the name and base URL, so a Read & Write server a source workflow reads from keeps its read config.
 */
describe('GenericFhirWriteConnectionFormComponent', () => {
  let create: jasmine.Spy;
  let update: jasmine.Spy;

  function render(existing: SourceConnectionModel | null) {
    create = jasmine.createSpy('create').and.returnValue(of({}));
    update = jasmine.createSpy('update').and.returnValue(of({}));
    TestBed.configureTestingModule({
      imports: [GenericFhirWriteConnectionFormComponent],
      providers: [
        { provide: ISourceConnectionService, useValue: { create, update } },
        { provide: ToastService, useValue: { success: () => undefined, show: () => undefined } },
      ],
    });
    const fixture = TestBed.createComponent(GenericFhirWriteConnectionFormComponent);
    fixture.componentRef.setInput('existing', existing);
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  it('creates a write-only, unauthenticated Backend connection', () => {
    const form = render(null);
    form.form.setValue({ name: 'HAPI', baseUrl: 'https://hapi.example/fhir' });

    form.save();

    const request = create.calls.mostRecent().args[0] as SourceConnectionRequest;
    expect(request.access).toBe('Write');
    expect(request.applicationType).toBe('Backend');
    expect(request.authentication).toEqual({ authenticationType: 'None', scopes: [] });
    expect(request.retrieval).toBeNull();
  });

  it('hands back the saved connection, so a caller can pick it at once', () => {
    const form = render(null);
    const created = { id: 'h9', name: 'HAPI', sourceSystemType: 'GenericFhir' } as SourceConnectionModel;
    create.and.returnValue(of(created));
    const saved = jasmine.createSpy('saved');
    form.saved.subscribe(saved);
    form.form.setValue({ name: 'HAPI', baseUrl: 'https://hapi.example/fhir' });

    form.save();

    expect(saved).toHaveBeenCalledWith(created);
  });

  it('editing a Read & Write server keeps its access, retrieval, authentication and application type', () => {
    const retrieval = { retrievalMethod: 'search-rest', resourceTypes: ['Patient'], searchCriteria: null, incrementalSyncEnabled: false };
    const form = render({
      id: 'h1', name: 'Local HAPI', sourceSystemType: 'GenericFhir', baseUrl: 'https://hapi.example/fhir', isEnabled: true,
      access: 'ReadWrite', applicationType: null, retrieval,
      authentication: { authenticationType: 'None', scopes: ['system/Patient.read'] },
    } as SourceConnectionModel);
    form.form.controls.name.setValue('Local HAPI (renamed)');

    form.save();

    const [id, request] = update.calls.mostRecent().args as [string, SourceConnectionRequest];
    expect(id).toBe('h1');
    expect(request.name).toBe('Local HAPI (renamed)');
    expect(request.access).toBeNull();
    expect(request.retrieval).toEqual(retrieval);
    expect(request.applicationType).toBeNull();
    expect(request.authentication.scopes).toEqual(['system/Patient.read']);
  });
});
