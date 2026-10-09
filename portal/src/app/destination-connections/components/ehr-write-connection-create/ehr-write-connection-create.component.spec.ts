import { TestBed } from '@angular/core/testing';
import { WritableSignal, signal } from '@angular/core';
import { EhrWriteConnectionCreateComponent } from './ehr-write-connection-create.component';
import { EhrWriteVendor } from '../../../connections/ehr-write-vendors';
import { WizardService } from '../../../services/wizard.service';
import { PermissionService } from '../../../auth/services/permission.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { SourceConnectionModel } from '../../../source-connections/models/source-connection.model';
import { ISourceConnectionService } from '../../../source-connections/services/i-source-connection.service';
import { EhrWriteConnectionEditorComponent } from '../ehr-write-connection-editor/ehr-write-connection-editor.component';

/** Creates one EHR write connection on the go and hands it back (an EHR Write-Back destination's picker uses it). */
describe('EhrWriteConnectionCreateComponent', () => {
  const EPIC = { id: 'w1', name: 'Epic write', sourceSystemType: 'Epic' } as SourceConnectionModel;
  const HAPI = { id: 'h1', name: 'HAPI', sourceSystemType: 'GenericFhir' } as SourceConnectionModel;

  let wiz: {
    isOpen: WritableSignal<boolean>; wizardMode: WritableSignal<string>; purpose: WritableSignal<string>;
    ehrType: WritableSignal<string>; saved: WritableSignal<number>; lastSavedEntity: WritableSignal<SourceConnectionModel | null>;
    openEntity: jasmine.Spy; close: jasmine.Spy;
  };
  let granted: Set<string>;

  function create(vendor: EhrWriteVendor | null, rights = ['ehrwriteback.create', 'epic.create', 'genericfhir.create']) {
    granted = new Set(rights);
    wiz = {
      isOpen: signal(false), wizardMode: signal('canvas'), purpose: signal('read'), ehrType: signal('Epic'), saved: signal(0),
      lastSavedEntity: signal<SourceConnectionModel | null>(null),
      openEntity: jasmine.createSpy('openEntity').and.callFake(() => {
        wiz.isOpen.set(true);
        wiz.wizardMode.set('entity');
        wiz.purpose.set('write');
      }),
      close: jasmine.createSpy('close').and.callFake(() => wiz.isOpen.set(false)),
    };
    TestBed.configureTestingModule({
      imports: [EhrWriteConnectionCreateComponent],
      providers: [
        { provide: WizardService, useValue: wiz },
        { provide: ISourceConnectionService, useValue: {} },
        { provide: PermissionService, useValue: { hasPermission: (c: string) => granted.has(c) } },
        { provide: PermissionActionGuard, useValue: { ensure: (codes: string[]) => codes.every(c => granted.has(c)) } },
      ],
    });
    // The editor's real vendor forms need the whole wizard; its logic (open, saved, closed) is what matters here.
    TestBed.overrideTemplate(EhrWriteConnectionEditorComponent, '');
    const fixture = TestBed.createComponent(EhrWriteConnectionCreateComponent);
    fixture.componentRef.setInput('vendor', vendor);
    const component = fixture.componentInstance;
    const created = jasmine.createSpy('created');
    const cancelled = jasmine.createSpy('cancelled');
    component.created.subscribe(created);
    component.cancelled.subscribe(cancelled);
    fixture.detectChanges();
    fixture.detectChanges();
    return { fixture, component, created, cancelled, el: fixture.nativeElement as HTMLElement };
  }

  it('a FHIR server opens its small form, and its save hands back the new connection', () => {
    const { fixture, created } = create('GenericFhir');
    const editor = fixture.debugElement.query(d => d.name === 'app-ehr-write-connection-editor')
      .componentInstance as EhrWriteConnectionEditorComponent;
    expect(editor.genericFhirEditing()).toBe('new');
    expect(wiz.openEntity).not.toHaveBeenCalled();

    editor.onGenericFhirSaved(HAPI);

    expect(created).toHaveBeenCalledOnceWith(HAPI);
  });

  it('an EHR opens the shared vendor form in write purpose, and its save hands back the new connection', () => {
    const { fixture, created, cancelled } = create('Epic');
    expect(wiz.openEntity).toHaveBeenCalledWith(null, { purpose: 'write' });
    expect(wiz.ehrType()).toBe('Epic');

    wiz.lastSavedEntity.set(EPIC);
    wiz.saved.set(1);
    wiz.isOpen.set(false);
    fixture.detectChanges();

    expect(created).toHaveBeenCalledOnceWith(EPIC);
    expect(cancelled).not.toHaveBeenCalled();
  });

  it('closing the vendor form without saving cancels', () => {
    const { fixture, created, cancelled } = create('Epic');
    wiz.isOpen.set(false);
    fixture.detectChanges();

    expect(cancelled).toHaveBeenCalledTimes(1);
    expect(created).not.toHaveBeenCalled();
  });

  it('with no vendor, offers a card per vendor the role may create for', () => {
    const { el } = create(null, ['ehrwriteback.create', 'epic.create']);
    const cards = Array.from(el.querySelectorAll('[data-testid="ewcc-vendor-card"]')).map(c => c.getAttribute('data-vendor'));
    expect(cards).toEqual(['Epic']);
  });

  it('a picked card opens that vendor\'s form', () => {
    const { component, fixture } = create(null);
    component.onCardPicked({ kind: 'ehr-write', value: 'Epic', label: 'Epic', sub: '', abbr: 'EP', color: '' });
    fixture.detectChanges();

    expect(component.choosing()).toBeFalse();
    expect(wiz.openEntity).toHaveBeenCalledWith(null, { purpose: 'write' });
  });

  it('cancels without the rights for the vendor', () => {
    const { cancelled } = create('Epic', ['ehrwriteback.create']);
    expect(wiz.openEntity).not.toHaveBeenCalled();
    expect(cancelled).toHaveBeenCalled();
  });

  it('destroying it closes an open write session', () => {
    const { fixture } = create('Epic');
    fixture.destroy();
    expect(wiz.close).toHaveBeenCalled();
  });
});
