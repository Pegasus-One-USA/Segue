import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { CdsTrustedIssuerListComponent } from './cds-trusted-issuer-list.component';
import { CdsTrustedIssuerDialogComponent } from '../../dialogs/cds-trusted-issuer-dialog/cds-trusted-issuer-dialog.component';
import { CdsCallbackTestDialogComponent } from '../../dialogs/cds-callback-test-dialog/cds-callback-test-dialog.component';
import { AuthStore } from '../../../auth/store/auth.store';
import { DialogService } from '../../../core/services/dialog.service';
import { ToastService } from '../../../services/toast.service';
import { CdsApiService } from '../../services/cds-api.service';
import { CdsTrustedIssuer } from '../../models/cds.models';
import {
  CDS_ALL_PERMISSIONS, makeCdsApiSpy, makeDialogSpy, makeIssuer, makeToastSpy, signInWith,
} from '../../testing/cds-test-data';

describe('CdsTrustedIssuerListComponent', () => {
  let api: ReturnType<typeof makeCdsApiSpy>;
  let toast: ReturnType<typeof makeToastSpy>;
  let dialog: ReturnType<typeof makeDialogSpy>;

  function create(issuers: CdsTrustedIssuer[], permissions = CDS_ALL_PERMISSIONS, dialogResult: unknown = undefined) {
    api = makeCdsApiSpy();
    toast = makeToastSpy();
    dialog = makeDialogSpy(dialogResult);
    TestBed.configureTestingModule({
      imports: [CdsTrustedIssuerListComponent],
      providers: [
        provideNoopAnimations(),
        { provide: CdsApiService, useValue: api },
        { provide: ToastService, useValue: toast },
        { provide: DialogService, useValue: dialog },
      ],
    });
    signInWith(TestBed.inject(AuthStore), permissions);
    const fixture = TestBed.createComponent(CdsTrustedIssuerListComponent);
    fixture.componentRef.setInput('issuers', issuers);
    fixture.detectChanges();
    const changed = jasmine.createSpy('changed');
    fixture.componentInstance.changed.subscribe(changed);
    return { fixture, list: fixture.componentInstance, el: fixture.nativeElement as HTMLElement, changed };
  }

  it('lists issuers with key source, audience and status', () => {
    const { el } = create([
      makeIssuer(),
      makeIssuer({ id: 'b', jwksUrl: null, jwksJson: '{"keys":[{}]}', audience: 'https://aud', isEnabled: false }),
    ]);

    const rows = el.querySelectorAll('[data-testid="cds-issuer-row"]');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('JWKS URL');
    expect(rows[0].textContent).toContain('Service URL');
    expect(rows[1].textContent).toContain('Pasted keys');
    expect(rows[1].textContent).toContain('https://aud');
    expect(rows[1].textContent).toContain('Off');
  });

  it('shows whether missing data is fetched and whether the client id is checked', () => {
    const { el } = create([
      makeIssuer({ expectedSubject: 'client-123' }),
      makeIssuer({ id: 'b', allowCallback: false }),
    ]);
    const rows = el.querySelectorAll('[data-testid="cds-issuer-row"]');
    expect(rows[0].textContent).toContain('Fetched from the EHR');
    expect(rows[0].textContent).toContain('Client id checked');
    expect(rows[1].textContent).toContain('Not fetched');
    expect(rows[1].textContent).not.toContain('Client id checked');
  });

  it('opens the callback test for an issuer', () => {
    const { list } = create([makeIssuer()]);
    const issuer = makeIssuer();
    list.openCallbackTest(issuer);
    expect(dialog.open).toHaveBeenCalledWith(CdsCallbackTestDialogComponent, jasmine.objectContaining({ data: { issuer } }));
  });

  it('will not open the callback test without the test permission', () => {
    const { list } = create([makeIssuer()], ['cdshooks.view', 'cdshooks.edit']);
    list.openCallbackTest(makeIssuer());
    expect(dialog.open).not.toHaveBeenCalled();
    expect(toast.warning).toHaveBeenCalled();
  });

  it('shows the row menu to a role that can only test', () => {
    const { el, list } = create([makeIssuer()], ['cdshooks.view', 'cdshooks.test']);
    expect(list.hasRowMenu()).toBeTrue();
    expect(el.querySelector('.btn-more')).not.toBeNull();
  });

  it('explains that every call is refused when there are no issuers', () => {
    expect(create([]).el.textContent).toContain('every EHR call is refused');
  });

  it('hides Add and the row menu from a view-only role', () => {
    const { el } = create([makeIssuer()], ['cdshooks.view']);
    expect(el.querySelector('[data-testid="cds-issuer-add"]')).toBeNull();
    expect(el.querySelector('.btn-more')).toBeNull();
  });

  it('opens the add dialog pre-filled for the sandbox and reports a save', () => {
    const { list, changed } = create([], CDS_ALL_PERMISSIONS, makeIssuer({ name: 'CDS Hooks sandbox' }));

    list.openAdd(true);

    expect(dialog.open).toHaveBeenCalledWith(CdsTrustedIssuerDialogComponent, jasmine.objectContaining({ data: { sandbox: true } }));
    expect(toast.success).toHaveBeenCalledWith('"CDS Hooks sandbox" added.');
    expect(changed).toHaveBeenCalled();
  });

  it('does not report a change when the dialog is cancelled', () => {
    const { list, changed } = create([makeIssuer()]);
    list.openEdit(makeIssuer());
    expect(changed).not.toHaveBeenCalled();
  });

  it('will not open add without create permission', () => {
    const { list } = create([], ['cdshooks.view']);
    list.openAdd();
    expect(dialog.open).not.toHaveBeenCalled();
    expect(toast.warning).toHaveBeenCalled();
  });

  it('deletes after confirmation, and shows the backend reason when the issuer is still in use', () => {
    const { list, changed } = create([makeIssuer()], CDS_ALL_PERMISSIONS, true);
    api.deleteTrustedIssuer.and.returnValue(of(undefined));
    list.confirmDelete(makeIssuer());
    expect(api.deleteTrustedIssuer).toHaveBeenCalledWith('iss-1');
    expect(changed).toHaveBeenCalledTimes(1);

    api.deleteTrustedIssuer.and.returnValue(throwError(() => Object.assign(
      new HttpErrorResponse({ status: 400 }), { message: 'Still allowed on: Renal dose check.' })));
    list.confirmDelete(makeIssuer());
    expect(toast.error).toHaveBeenCalledWith('Still allowed on: Renal dose check.');
    expect(changed).toHaveBeenCalledTimes(1);
  });
});
