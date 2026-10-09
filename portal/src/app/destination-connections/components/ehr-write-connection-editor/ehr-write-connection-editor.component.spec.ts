import { TestBed } from '@angular/core/testing';
import { WritableSignal, signal } from '@angular/core';
import { EhrWriteConnectionEditorComponent } from './ehr-write-connection-editor.component';
import { WizardService } from '../../../services/wizard.service';
import { SourceConnectionModel } from '../../../source-connections/models/source-connection.model';
import { ISourceConnectionService } from '../../../source-connections/services/i-source-connection.service';

/** Opens an EHR write connection's form: the shared vendor form in write purpose, or the FHIR server form. */
describe('EhrWriteConnectionEditorComponent', () => {
  const ATHENA = {
    id: 'a1', name: 'athena write', sourceSystemType: 'Athenahealth', baseUrl: 'https://api.athena.example/fhir/r4',
    isEnabled: true, access: 'Write', authentication: { authenticationType: 'OAuthClientCredentials', scopes: [] },
  } as SourceConnectionModel;
  const HAPI = {
    id: 'h1', name: 'Local HAPI', sourceSystemType: 'GenericFhir', baseUrl: 'https://hapi.example/fhir',
    isEnabled: true, access: 'ReadWrite', authentication: { authenticationType: 'None', scopes: [] },
  } as SourceConnectionModel;

  let wiz: {
    isOpen: WritableSignal<boolean>; wizardMode: WritableSignal<string>; purpose: WritableSignal<string>;
    ehrType: WritableSignal<string>; saved: WritableSignal<number>; lastSavedEntity: WritableSignal<SourceConnectionModel | null>;
    openEntity: jasmine.Spy; close: jasmine.Spy;
  };

  function create(stubTemplate = false) {
    wiz = {
      isOpen: signal(false), wizardMode: signal('canvas'), purpose: signal('read'), ehrType: signal('Epic'), saved: signal(0),
      lastSavedEntity: signal<SourceConnectionModel | null>(null),
      openEntity: jasmine.createSpy('openEntity'), close: jasmine.createSpy('close'),
    };
    TestBed.configureTestingModule({
      imports: [EhrWriteConnectionEditorComponent],
      providers: [
        { provide: WizardService, useValue: wiz },
        { provide: ISourceConnectionService, useValue: {} },
      ],
    });
    // The real vendor forms need the whole wizard; a test that only follows the session's state can skip them.
    if (stubTemplate) TestBed.overrideTemplate(EhrWriteConnectionEditorComponent, '');
    const fixture = TestBed.createComponent(EhrWriteConnectionEditorComponent);
    fixture.detectChanges();
    return { fixture, editor: fixture.componentInstance, el: fixture.nativeElement as HTMLElement };
  }

  it('opens a new vendor connection in write purpose', () => {
    const { editor } = create();

    editor.openNew('Healow');

    expect(wiz.openEntity).toHaveBeenCalledWith(null, { purpose: 'write' });
    expect(wiz.ehrType()).toBe('Healow');
  });

  it('opens a new FHIR server in its own form', () => {
    const { fixture, editor, el } = create();

    editor.openNew('GenericFhir');
    fixture.detectChanges();

    expect(wiz.openEntity).not.toHaveBeenCalled();
    expect(editor.genericFhirEditing()).toBe('new');
    expect(el.querySelector('app-generic-fhir-write-connection-form')).not.toBeNull();
  });

  it('edits a vendor connection in write purpose, and a FHIR server in its own form', () => {
    const { fixture, editor, el } = create();

    editor.open(ATHENA, false);
    expect(wiz.openEntity).toHaveBeenCalledWith(ATHENA, { readonly: false, purpose: 'write' });

    editor.open(HAPI, true);
    fixture.detectChanges();
    expect(editor.genericFhirEditing()).toBe(HAPI);
    expect(editor.genericFhirReadonly()).toBeTrue();
    expect(el.querySelector('app-generic-fhir-write-connection-form')).not.toBeNull();
  });

  it('reports saved after a vendor form save, not on creation', () => {
    const { fixture, editor } = create();
    const saved = jasmine.createSpy('saved');
    editor.saved.subscribe(saved);
    fixture.detectChanges();
    expect(saved).not.toHaveBeenCalled();

    wiz.lastSavedEntity.set(ATHENA);
    wiz.saved.set(1);
    fixture.detectChanges();

    expect(saved).toHaveBeenCalledWith(ATHENA);
  });

  it('hands back the saved FHIR server and reports the form closed', () => {
    const { fixture, editor } = create();
    const saved = jasmine.createSpy('saved');
    const closed = jasmine.createSpy('closed');
    editor.saved.subscribe(saved);
    editor.closed.subscribe(closed);
    editor.openNew('GenericFhir');
    fixture.detectChanges();

    editor.onGenericFhirSaved(HAPI);

    expect(saved).toHaveBeenCalledWith(HAPI);
    expect(closed).toHaveBeenCalled();
    expect(editor.genericFhirEditing()).toBeNull();
  });

  it('reports closed when a write session it showed ends', () => {
    const { fixture, editor } = create(true);
    const closed = jasmine.createSpy('closed');
    editor.closed.subscribe(closed);
    wiz.isOpen.set(true);
    wiz.wizardMode.set('entity');
    wiz.purpose.set('write');
    fixture.detectChanges();
    expect(closed).not.toHaveBeenCalled();

    wiz.isOpen.set(false);
    fixture.detectChanges();

    expect(closed).toHaveBeenCalledTimes(1);
  });

  it('shows the vendor form only for a write-purpose entity session', () => {
    const { editor } = create();
    wiz.isOpen.set(true);
    wiz.wizardMode.set('entity');
    expect(editor.vendorFormOpen()).toBeFalse();

    wiz.purpose.set('write');
    expect(editor.vendorFormOpen()).toBeTrue();
  });

  it('on destroy closes a write session only', () => {
    const { fixture } = create();
    wiz.isOpen.set(true);
    wiz.wizardMode.set('entity');
    fixture.destroy();
    expect(wiz.close).not.toHaveBeenCalled();

    TestBed.resetTestingModule();
    const second = create();
    wiz.isOpen.set(true);
    wiz.wizardMode.set('entity');
    wiz.purpose.set('write');
    second.fixture.destroy();
    expect(wiz.close).toHaveBeenCalled();
  });
});
