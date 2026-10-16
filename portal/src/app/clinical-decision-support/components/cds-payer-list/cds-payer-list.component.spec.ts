import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { CdsPayerListComponent } from './cds-payer-list.component';
import { CdsPayerDialogComponent } from '../../dialogs/cds-payer-dialog/cds-payer-dialog.component';
import { CdsRelayTestDialogComponent } from '../../dialogs/cds-relay-test-dialog/cds-relay-test-dialog.component';
import { ConfirmDialogComponent } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { AuthStore } from '../../../auth/store/auth.store';
import { DialogService } from '../../../core/services/dialog.service';
import { ToastService } from '../../../services/toast.service';
import { CdsCoverageApiService } from '../../services/cds-coverage-api.service';
import { CdsPayer } from '../../models/cds-coverage.models';
import {
  CDS_ALL_PERMISSIONS, makeCoverageApiSpy, makeDialogSpy, makePayer, makeToastSpy, signInWith,
} from '../../testing/cds-test-data';

describe('CdsPayerListComponent', () => {
  let api: ReturnType<typeof makeCoverageApiSpy>;
  let toast: ReturnType<typeof makeToastSpy>;
  let dialog: ReturnType<typeof makeDialogSpy>;

  function create(payers: CdsPayer[], permissions = CDS_ALL_PERMISSIONS, dialogResult: unknown = undefined) {
    api = makeCoverageApiSpy();
    toast = makeToastSpy();
    dialog = makeDialogSpy(dialogResult);
    TestBed.configureTestingModule({
      imports: [CdsPayerListComponent],
      providers: [
        provideNoopAnimations(),
        { provide: CdsCoverageApiService, useValue: api },
        { provide: ToastService, useValue: toast },
        { provide: DialogService, useValue: dialog },
      ],
    });
    signInWith(TestBed.inject(AuthStore), permissions);
    const fixture = TestBed.createComponent(CdsPayerListComponent);
    fixture.componentRef.setInput('payers', payers);
    fixture.detectChanges();
    const changed = jasmine.createSpy('changed');
    fixture.componentInstance.changed.subscribe(changed);
    return { fixture, list: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, changed };
  }

  it('lists payers with identifiers, plans, rules and how they are answered', () => {
    const { el } = create([
      makePayer({ identifiers: ['ACME01', 'Acme'], planValues: [], ruleCount: 3, enabledRuleCount: 2 }),
      makePayer({
        id: 'b', name: 'Beta', planValues: ['GOLD'], isEnabled: false, relayEnabled: true,
        relayServiceUrl: 'https://crd.beta.example/svc', fallbackToLocalRules: false, forwardFhirAuthorization: true,
      }),
    ]);
    const rows = el.querySelectorAll('[data-testid="cds-payer-row"]');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('ACME01');
    expect(rows[0].textContent).toContain('Every plan');
    expect(rows[0].textContent).toContain('2 on');
    expect(rows[0].textContent).toContain('3 in all');
    expect(rows[0].textContent).toContain('Rules in Segue');
    expect(rows[1].textContent).toContain('GOLD');
    expect(rows[1].textContent).toContain("Payer's service only");
    expect(rows[1].textContent).toContain('https://crd.beta.example/svc');
    expect(rows[1].textContent).toContain('Gets EHR token');
    expect(rows[1].textContent).toContain('Off');
  });

  it('explains what to do when there are no payers', () => {
    expect(create([]).el.querySelector('[data-testid="cds-payer-empty"]')?.textContent).toContain('Add the first payer');
  });

  it('hides Add and the row menu from a view-only role', () => {
    const { el } = create([makePayer()], ['cdshooks.view']);
    expect(el.querySelector('[data-testid="cds-payer-add"]')).toBeNull();
    expect(el.querySelector('.btn-more')).toBeNull();
  });

  it('gives a test-only role a menu only for relayed payers', () => {
    const { list } = create([makePayer()], ['cdshooks.view', 'cdshooks.test']);
    expect(list.hasRowMenu(makePayer())).toBeFalse();
    expect(list.hasRowMenu(makePayer({ relayEnabled: true }))).toBeTrue();
  });

  it('opens the add dialog and reports a save', () => {
    const { list, changed } = create([], CDS_ALL_PERMISSIONS, makePayer({ name: 'Acme Health Plan' }));
    list.openAdd();
    expect(dialog.open).toHaveBeenCalledWith(CdsPayerDialogComponent, jasmine.objectContaining({ data: {} }));
    expect(toast.success).toHaveBeenCalledWith('"Acme Health Plan" added.');
    expect(changed).toHaveBeenCalled();
  });

  it('opens the edit dialog and does nothing when it is cancelled', () => {
    const { list, changed } = create([makePayer()]);
    list.openEdit(makePayer());
    expect(dialog.open).toHaveBeenCalledWith(CdsPayerDialogComponent, jasmine.objectContaining({ data: { payer: makePayer() } }));
    expect(changed).not.toHaveBeenCalled();
  });

  it('opens the relay test, and refuses it without the test permission', () => {
    const payer = makePayer({ relayEnabled: true });
    const { list } = create([payer]);
    list.openRelayTest(payer);
    expect(dialog.open).toHaveBeenCalledWith(CdsRelayTestDialogComponent, jasmine.objectContaining({ data: { payer } }));

    TestBed.resetTestingModule();
    const limited = create([payer], ['cdshooks.view', 'cdshooks.edit']);
    limited.list.openRelayTest(payer);
    expect(dialog.open).not.toHaveBeenCalled();
    expect(toast.warning).toHaveBeenCalled();
  });

  it('says how many rules a delete removes, and deletes after confirmation', () => {
    const { list, changed } = create([makePayer()], CDS_ALL_PERMISSIONS, true);
    api.deletePayer.and.returnValue(of(undefined));
    list.confirmDelete(makePayer({ ruleCount: 4 }));
    expect(dialog.open).toHaveBeenCalledWith(ConfirmDialogComponent, jasmine.objectContaining({
      data: jasmine.objectContaining({ message: jasmine.stringContaining('its 4 rules') }),
    }));
    expect(api.deletePayer).toHaveBeenCalledWith('payer-1');
    expect(changed).toHaveBeenCalledTimes(1);

    api.deletePayer.and.returnValue(throwError(() => Object.assign(new HttpErrorResponse({ status: 404 }), { message: 'Not found.' })));
    list.confirmDelete(makePayer());
    expect(toast.error).toHaveBeenCalledWith('Not found.');
    expect(changed).toHaveBeenCalledTimes(1);
  });

  it('will not open add without create permission', () => {
    const { list } = create([], ['cdshooks.view']);
    list.openAdd();
    expect(dialog.open).not.toHaveBeenCalled();
    expect(toast.warning).toHaveBeenCalled();
  });
});
