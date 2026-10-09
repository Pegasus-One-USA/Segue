import { TestBed } from '@angular/core/testing';
import { WritableSignal, signal } from '@angular/core';
import { of } from 'rxjs';
import { EhrWriteConnectionKindComponent } from './ehr-write-connection-kind.component';
import { ISourceConnectionService } from '../../../source-connections/services/i-source-connection.service';
import { SourceConnectionModel } from '../../../source-connections/models/source-connection.model';
import { WizardService } from '../../../services/wizard.service';
import { PermissionService } from '../../../auth/services/permission.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { DialogService } from '../../../core/services/dialog.service';
import { ToastService } from '../../../services/toast.service';
import { ConnectionRow } from '../../../connections/connection-row.model';

/**
 * EHR write connections on Destination Connections: lists only connections that can write (access=write), offers
 * Epic / eClinicalWorks / athenahealth / FHIR server, and locks Delete while a write-back still uses the connection.
 */
describe('EhrWriteConnectionKindComponent', () => {
  const ATHENA = {
    id: 'a1', name: 'athena write', sourceSystemType: 'Athenahealth', baseUrl: 'https://api.athena.example/fhir/r4',
    isEnabled: true, access: 'Write', vendorWriteApisActivated: true,
    authentication: { authenticationType: 'OAuthClientCredentials', scopes: [] },
  } as SourceConnectionModel;
  const HAPI = {
    id: 'h1', name: 'Local HAPI', sourceSystemType: 'GenericFhir', baseUrl: 'https://hapi.example/fhir',
    isEnabled: true, access: 'ReadWrite', authentication: { authenticationType: 'None', scopes: [] },
  } as SourceConnectionModel;

  let svc: { getAll: jasmine.Spy; getUsedIds: jasmine.Spy; getWriteUsedIds: jasmine.Spy; delete: jasmine.Spy };
  let wiz: {
    isOpen: WritableSignal<boolean>; wizardMode: WritableSignal<string>; purpose: WritableSignal<string>;
    ehrType: WritableSignal<string>; saved: WritableSignal<number>; lastSavedEntity: WritableSignal<SourceConnectionModel | null>;
    openEntity: jasmine.Spy; close: jasmine.Spy;
  };
  let dialog: { open: jasmine.Spy };

  function create(opts: { granted?: (code: string) => boolean; usedWrite?: string[]; usedRead?: string[] } = {}) {
    const granted = opts.granted ?? (() => true);
    svc = {
      getAll: jasmine.createSpy('getAll').and.returnValue(of([ATHENA, HAPI])),
      getUsedIds: jasmine.createSpy('getUsedIds').and.returnValue(of(opts.usedRead ?? [])),
      getWriteUsedIds: jasmine.createSpy('getWriteUsedIds').and.returnValue(of(opts.usedWrite ?? [])),
      delete: jasmine.createSpy('delete').and.returnValue(of(undefined)),
    };
    wiz = {
      isOpen: signal(false), wizardMode: signal('canvas'), purpose: signal('read'), ehrType: signal('Epic'), saved: signal(0),
      lastSavedEntity: signal<SourceConnectionModel | null>(null),
      openEntity: jasmine.createSpy('openEntity'), close: jasmine.createSpy('close'),
    };
    dialog = { open: jasmine.createSpy('open').and.returnValue({ afterClosed: () => of(true) }) };
    TestBed.configureTestingModule({
      imports: [EhrWriteConnectionKindComponent],
      providers: [
        { provide: ISourceConnectionService, useValue: svc },
        { provide: WizardService, useValue: wiz },
        { provide: PermissionService, useValue: { hasPermission: granted, hasAll: (codes: readonly string[]) => codes.every(granted) } },
        { provide: PermissionActionGuard, useValue: { ensure: () => true } },
        { provide: DialogService, useValue: dialog },
        { provide: ToastService, useValue: { success: () => undefined, error: () => undefined } },
      ],
    });
    const fixture = TestBed.createComponent(EhrWriteConnectionKindComponent);
    fixture.detectChanges();
    return { fixture, kind: fixture.componentInstance };
  }

  function rows(kind: EhrWriteConnectionKindComponent): ConnectionRow[] {
    let result: ConnectionRow[] = [];
    kind.list().subscribe(r => (result = r));
    return result;
  }

  const ids = (kind: EhrWriteConnectionKindComponent, row: ConnectionRow) => kind.rowActions(row).map(a => a.id);

  it('lists the connections that can write, with EHR label and activation status', () => {
    const { kind } = create();
    const [athena, hapi] = rows(kind);

    expect(svc.getAll).toHaveBeenCalledWith('write');
    expect(athena).toEqual(jasmine.objectContaining({
      kind: 'ehr-write', typeLabel: 'EHR write connection — athenahealth', writeApis: 'Activated',
      filterKeys: ['kind:ehr-write', 'ehr-write:Athenahealth'],
    }));
    expect(hapi.typeLabel).toBe('EHR write connection — FHIR server');
    expect(hapi.writeApis).toBeNull();
  });

  it('offers Epic, eClinicalWorks, athenahealth and FHIR server cards', () => {
    const { kind } = create();

    expect(kind.createCards().map(c => c.value)).toEqual(['Epic', 'Healow', 'Athenahealth', 'GenericFhir']);
    expect(kind.createCards().map(c => c.label)).toContain('eClinicalWorks');
  });

  it('a card opens the editor in write purpose', () => {
    const { kind } = create();
    const card = kind.createCards().find(c => c.value === 'Healow')!;

    expect(kind.create(card)).toBeTrue();
    expect(wiz.openEntity).toHaveBeenCalledWith(null, { purpose: 'write' });
    expect(wiz.ehrType()).toBe('Healow');
  });

  it('offers no card without ehrwriteback.create or a vendor create right', () => {
    expect(create({ granted: c => c !== 'ehrwriteback.create' }).kind.createCards()).toEqual([]);
    TestBed.resetTestingModule();
    expect(create({ granted: c => c !== 'epic.create' }).kind.createCards().map(c => c.value)).not.toContain('Epic');
  });

  it('cannot list, and offers nothing, without sourceconnections.view', () => {
    const { kind } = create({ granted: code => code !== 'sourceconnections.view' });

    expect(kind.canList()).toBeFalse();
    expect(kind.createCards()).toEqual([]);
  });

  it('cannot list, and offers nothing, without ehrwriteback.view', () => {
    const { kind } = create({ granted: code => code !== 'ehrwriteback.view' });

    expect(kind.canList()).toBeFalse();
    expect(kind.createCards()).toEqual([]);
  });

  it('offers View always, Edit with both edit rights, and no Test', () => {
    const { kind } = create({ granted: code => code !== 'ehrwriteback.edit' });
    const [athena] = rows(kind);

    expect(ids(kind, athena)).toEqual(['view', 'delete']);
  });

  it('needs all three delete rights for Delete', () => {
    const { kind } = create({ granted: code => code !== 'ehrwriteback.delete' });
    const [athena] = rows(kind);

    expect(ids(kind, athena)).not.toContain('delete');
  });

  it('locks Delete by write usage, and by read usage only for a Read & Write row', () => {
    const { kind } = create({ usedWrite: ['a1'], usedRead: ['h1'] });
    const [athena, hapi] = rows(kind);
    const del = (row: ConnectionRow) => kind.rowActions(row).find(a => a.id === 'delete')!;

    expect(del(athena)).toEqual(jasmine.objectContaining({ disabled: true, label: 'Delete (used by a workflow)' }));
    expect(del(hapi).disabled).toBeTrue();

    TestBed.resetTestingModule();
    const writeOnlyReadUse = create({ usedRead: ['a1'] });
    const [athenaAgain] = rows(writeOnlyReadUse.kind);
    expect(writeOnlyReadUse.kind.rowActions(athenaAgain).find(a => a.id === 'delete')!.disabled).toBeFalse();
  });

  it('deletes after the confirm and reports a change', () => {
    const { kind } = create();
    const [, hapi] = rows(kind);
    const changed = jasmine.createSpy('changed');
    kind.changed.subscribe(changed);

    kind.runAction('delete', hapi);

    expect(dialog.open.calls.mostRecent().args[1].data.message).toContain('removes it from Source Connections too');
    expect(svc.delete).toHaveBeenCalledWith('h1');
    expect(changed).toHaveBeenCalled();
  });

  it('reports a change after a vendor form save', () => {
    const { fixture, kind } = create();
    const changed = jasmine.createSpy('changed');
    kind.changed.subscribe(changed);

    wiz.saved.set(1);
    fixture.detectChanges();

    expect(changed).toHaveBeenCalled();
  });
});
