import { TestBed } from '@angular/core/testing';
import { WritableSignal, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { Observable, of, throwError } from 'rxjs';
import { EhrReadConnectionKindComponent } from './ehr-read-connection-kind.component';
import { ISourceConnectionService } from '../../services/i-source-connection.service';
import { SourceConnectionModel } from '../../models/source-connection.model';
import { WizardService } from '../../../services/wizard.service';
import { PermissionService } from '../../../auth/services/permission.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { DialogService } from '../../../core/services/dialog.service';
import { ToastService } from '../../../services/toast.service';
import { ConnectionRow } from '../../../connections/connection-row.model';

/** EHR / FHIR read connections on Source Connections: rows, New cards, the read-purpose form and the in-use locks. */
describe('EhrReadConnectionKindComponent', () => {
  const ECW = {
    id: 'e1', name: 'ECW', sourceSystemType: 'Healow', baseUrl: 'https://ecw.example/fhir', applicationType: 'Patient',
    isEnabled: true, access: 'Read', createdBy: 'Ann', createdOnUtc: '2026-01-01T00:00:00Z', modifiedBy: 'Bob',
    modifiedOnUtc: '2026-02-01T00:00:00Z', authentication: { authenticationType: 'None', clientId: 'cid', scopes: [] },
  } as SourceConnectionModel;
  const BOTH = { ...ECW, id: 'rw1', name: 'Athena RW', sourceSystemType: 'Athenahealth', access: 'ReadWrite' } as SourceConnectionModel;

  let wiz: {
    isOpen: WritableSignal<boolean>; wizardMode: WritableSignal<string>; purpose: WritableSignal<string>;
    ehrType: WritableSignal<string>; saved: WritableSignal<number>; openEntity: jasmine.Spy; close: jasmine.Spy;
  };
  let svc: { getAll: jasmine.Spy; getUsedIds: jasmine.Spy; getWriteUsedIds: jasmine.Spy; delete: jasmine.Spy };
  let toast: { success: jasmine.Spy; error: jasmine.Spy };
  let dialog: { open: jasmine.Spy };

  function create(opts: {
    granted?: (code: string) => boolean;
    usedRead?: Observable<string[]>;
    usedWrite?: Observable<string[]>;
  } = {}) {
    const granted = opts.granted ?? (() => true);
    wiz = {
      isOpen: signal(false), wizardMode: signal('canvas'), purpose: signal('read'), ehrType: signal('Epic'), saved: signal(0),
      openEntity: jasmine.createSpy('openEntity').and.callFake(() => wiz.isOpen.set(true)), close: jasmine.createSpy('close'),
    };
    svc = {
      getAll: jasmine.createSpy('getAll').and.returnValue(of([ECW, BOTH])),
      getUsedIds: jasmine.createSpy('getUsedIds').and.returnValue(opts.usedRead ?? of([])),
      getWriteUsedIds: jasmine.createSpy('getWriteUsedIds').and.returnValue(opts.usedWrite ?? of([])),
      delete: jasmine.createSpy('delete').and.returnValue(of(undefined)),
    };
    toast = { success: jasmine.createSpy('success'), error: jasmine.createSpy('error') };
    dialog = { open: jasmine.createSpy('open').and.returnValue({ afterClosed: () => of(true) }) };
    TestBed.configureTestingModule({
      imports: [EhrReadConnectionKindComponent],
      providers: [
        { provide: ISourceConnectionService, useValue: svc },
        { provide: WizardService, useValue: wiz },
        { provide: PermissionService, useValue: { hasPermission: granted, hasAll: (codes: readonly string[]) => codes.every(granted) } },
        { provide: PermissionActionGuard, useValue: { ensure: () => true } },
        { provide: DialogService, useValue: dialog },
        { provide: ToastService, useValue: toast },
      ],
    });
    const fixture = TestBed.createComponent(EhrReadConnectionKindComponent);
    fixture.detectChanges();
    return { fixture, kind: fixture.componentInstance };
  }

  function rows(kind: EhrReadConnectionKindComponent): ConnectionRow[] {
    let result: ConnectionRow[] = [];
    kind.list().subscribe(r => (result = r));
    return result;
  }

  const action = (kind: EhrReadConnectionKindComponent, row: ConnectionRow, id: string) =>
    kind.rowActions(row).find(a => a.id === id);

  it('lists read connections as rows', () => {
    const { kind } = create();
    const [ecw] = rows(kind);

    expect(svc.getAll).toHaveBeenCalledWith('read');
    expect(ecw).toEqual(jasmine.objectContaining({
      kind: 'ehr-read', key: 'ehr-read:e1', typeLabel: 'eClinicalWorks', audience: 'Patient', address: 'https://ecw.example/fhir',
      clientId: 'cid', isEnabled: true, actionBy: 'Bob', actionOn: '2026-02-01T00:00:00Z',
      filterKeys: ['kind:ehr-read', 'vendor:Healow', 'audience:Patient'],
    }));
  });

  it('cannot list without sourceconnections.view', () => {
    expect(create({ granted: code => code !== 'sourceconnections.view' }).kind.canList()).toBeFalse();
    TestBed.resetTestingModule();
    expect(create().kind.canList()).toBeTrue();
  });

  it('offers New cards only for vendors with their own form and the role\'s create right', () => {
    const { kind } = create({ granted: code => code !== 'healow.create' });
    const vendors = kind.createCards().map(c => c.value);

    expect(vendors).toContain('Epic');
    expect(vendors).toContain('Athenahealth');
    expect(vendors).not.toContain('Healow');
    expect(vendors).not.toContain('GenericFhir');
    expect(vendors).not.toContain('Hl7v2');
    expect(kind.createCards().every(c => c.kind === 'ehr-read')).toBeTrue();
  });

  it('create opens a read-purpose entity session for the chosen vendor', () => {
    const { kind } = create();
    const card = kind.createCards().find(c => c.value === 'Athenahealth')!;

    expect(kind.create(card)).toBeTrue();
    expect(wiz.openEntity).toHaveBeenCalledWith(null);
    expect(wiz.ehrType()).toBe('Athenahealth');
  });

  it('locks Edit by read usage only, and Delete by read or write usage', () => {
    const { kind } = create({ usedRead: of(['e1']), usedWrite: of(['rw1']) });
    const [ecw, both] = rows(kind);

    expect(action(kind, ecw, 'edit')).toEqual(jasmine.objectContaining({ disabled: true, label: 'Edit (used by a workflow)' }));
    expect(action(kind, ecw, 'delete')).toEqual(jasmine.objectContaining({ disabled: true, label: 'Delete (used by a workflow)' }));
    expect(action(kind, both, 'edit')).toEqual(jasmine.objectContaining({ disabled: false, label: 'Edit' }));
    expect(action(kind, both, 'delete')).toEqual(jasmine.objectContaining({ disabled: true, danger: true }));
  });

  it('hides Delete without every delete right (ehrwriteback.delete for a Read & Write row)', () => {
    const { kind } = create({ granted: code => code !== 'ehrwriteback.delete' });
    const [ecw, both] = rows(kind);

    expect(action(kind, ecw, 'delete')).toBeDefined();
    expect(action(kind, both, 'delete')).toBeUndefined();
  });

  it('shows no usage toast for a 401 / 403 on either usage call', () => {
    const denied = throwError(() => new HttpErrorResponse({ status: 403 }));
    const { kind } = create({ usedRead: denied, usedWrite: throwError(() => new HttpErrorResponse({ status: 401 })) });
    rows(kind);

    expect(toast.error).not.toHaveBeenCalled();
  });

  it('shows one usage toast when both usage calls fail for real', () => {
    const failed = () => throwError(() => new HttpErrorResponse({ status: 500 }));
    const { kind } = create({ usedRead: failed(), usedWrite: failed() });
    rows(kind);

    expect(toast.error).toHaveBeenCalledTimes(1);
  });

  it('warns that deleting a Read & Write connection removes it from Destination Connections too', () => {
    const { kind } = create();
    const [ecw, both] = rows(kind);
    const changed = jasmine.createSpy('changed');
    kind.changed.subscribe(changed);

    kind.runAction('delete', both);
    expect(dialog.open.calls.mostRecent().args[1].data.message).toContain('removes it from Destination Connections too');
    expect(svc.delete).toHaveBeenCalledWith('rw1');
    expect(changed).toHaveBeenCalled();

    kind.runAction('delete', ecw);
    expect(dialog.open.calls.mostRecent().args[1].data.message).not.toContain('Destination Connections');
  });

  it('emits changed after a save', () => {
    const { fixture, kind } = create();
    const changed = jasmine.createSpy('changed');
    kind.changed.subscribe(changed);

    wiz.saved.set(1);
    fixture.detectChanges();

    expect(changed).toHaveBeenCalled();
  });

  it('shows the form for a read-purpose entity session only', () => {
    const { kind } = create();
    wiz.isOpen.set(true);
    wiz.wizardMode.set('entity');
    wiz.purpose.set('write');
    expect(kind.formOpen()).toBeFalse();

    wiz.purpose.set('read');
    expect(kind.formOpen()).toBeTrue();
  });
});
