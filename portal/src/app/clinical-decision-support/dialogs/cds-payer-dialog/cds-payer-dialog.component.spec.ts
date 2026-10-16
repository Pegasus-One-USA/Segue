import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { CdsPayerDialogComponent, CdsPayerDialogData } from './cds-payer-dialog.component';
import { DIALOG_DATA, DialogRef, DialogService } from '../../../core/services/dialog.service';
import { AuthStore } from '../../../auth/store/auth.store';
import { ToastService } from '../../../services/toast.service';
import { CdsCoverageApiService } from '../../services/cds-coverage-api.service';
import {
  CDS_ALL_PERMISSIONS, makeCoverageApiSpy, makeDialogSpy, makePayer, makeToastSpy, signInWith,
} from '../../testing/cds-test-data';

describe('CdsPayerDialogComponent', () => {
  let api: ReturnType<typeof makeCoverageApiSpy>;
  let ref: { close: jasmine.Spy; attemptClose: jasmine.Spy };
  let toast: ReturnType<typeof makeToastSpy>;

  function create(data: CdsPayerDialogData | null, permissions = CDS_ALL_PERMISSIONS) {
    api = makeCoverageApiSpy();
    toast = makeToastSpy();
    ref = { close: jasmine.createSpy('close'), attemptClose: jasmine.createSpy('attemptClose') };
    TestBed.configureTestingModule({
      imports: [CdsPayerDialogComponent],
      providers: [
        { provide: CdsCoverageApiService, useValue: api },
        { provide: ToastService, useValue: toast },
        { provide: DialogService, useValue: makeDialogSpy(true) },
        { provide: DialogRef, useValue: ref },
        { provide: DIALOG_DATA, useValue: data },
      ],
    });
    signInWith(TestBed.inject(AuthStore), permissions);
    const fixture = TestBed.createComponent(CdsPayerDialogComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const submit = () => {
      (el.querySelector('[data-testid="cds-payer-dialog-save"]') as HTMLButtonElement).click();
      fixture.detectChanges();
    };
    return { fixture, dialog: fixture.componentInstance, el, submit };
  }

  it('needs a name and at least one identifier', () => {
    const { el, submit } = create(null);
    submit();
    expect(api.createPayer).not.toHaveBeenCalled();
    expect(el.textContent).toContain("Enter the payer's name.");
    expect(el.textContent).toContain('Add at least one value.');
  });

  it('creates a payer with local rules and the token off by default', () => {
    const { dialog, submit } = create(null);
    const saved = makePayer();
    api.createPayer.and.returnValue(of(saved));
    dialog.form.patchValue({ name: '  Acme Health Plan ', identifiers: ['ACME01', 'Acme'], planValues: ['GOLD'] });

    submit();

    expect(api.createPayer).toHaveBeenCalledOnceWith({
      name: 'Acme Health Plan', identifiers: ['ACME01', 'Acme'], planValues: ['GOLD'], isEnabled: true,
      relayEnabled: false, relayServiceUrl: null, relayTimeoutMs: 1000, fallbackToLocalRules: true,
      forwardFhirAuthorization: false, relayClientId: null,
    });
    expect(ref.close).toHaveBeenCalledWith(saved);
  });

  it('requires an https address without a query once the relay is on', () => {
    const { dialog, el, fixture, submit } = create(null);
    dialog.form.patchValue({ name: 'Acme', identifiers: ['ACME01'], relayEnabled: true });
    fixture.detectChanges();
    submit();
    expect(el.textContent).toContain("Enter the payer's service address.");

    dialog.form.controls.relayServiceUrl.setValue('http://crd.example.org');
    fixture.detectChanges();
    expect(el.textContent).toContain('starting with https://');

    dialog.form.controls.relayServiceUrl.setValue('https://crd.example.org/svc?x=1');
    fixture.detectChanges();
    expect(dialog.form.controls.relayServiceUrl.hasError('query')).toBeTrue();
    expect(api.createPayer).not.toHaveBeenCalled();
  });

  it('drops the address requirement when the relay is turned off again', () => {
    const { dialog } = create(null);
    dialog.form.patchValue({ name: 'Acme', identifiers: ['ACME01'], relayEnabled: true });
    expect(dialog.form.valid).toBeFalse();
    dialog.form.controls.relayEnabled.setValue(false);
    expect(dialog.form.valid).toBeTrue();
  });

  it('turning the relay off clears hidden relay errors so Save still works', () => {
    const { dialog, submit } = create(null);
    api.createPayer.and.returnValue(of(makePayer()));
    dialog.form.patchValue({ name: 'Acme', identifiers: ['ACME01'], relayEnabled: true });
    dialog.form.controls.relayServiceUrl.setValue('https://crd.example.org/svc?x=1');
    dialog.form.controls.relayTimeoutMs.setValue(5);

    dialog.form.controls.relayEnabled.setValue(false);
    submit();

    expect(dialog.form.valid).toBeTrue();
    expect(api.createPayer).toHaveBeenCalledWith(jasmine.objectContaining({ relayEnabled: false, relayServiceUrl: null, relayTimeoutMs: 1000 }));
  });

  it('keeps a valid relay address when the relay is switched off and on again', () => {
    const { dialog } = create(null);
    dialog.form.patchValue({ relayEnabled: true, relayServiceUrl: 'https://crd.example.org/svc' });
    dialog.form.controls.relayEnabled.setValue(false);
    dialog.form.controls.relayEnabled.setValue(true);
    expect(dialog.form.controls.relayServiceUrl.value).toBe('https://crd.example.org/svc');
  });

  it('keeps the relay timeout in range', () => {
    const { dialog } = create(null);
    dialog.form.controls.relayTimeoutMs.setValue(50);
    expect(dialog.form.controls.relayTimeoutMs.invalid).toBeTrue();
    dialog.form.controls.relayTimeoutMs.setValue(20000);
    expect(dialog.form.controls.relayTimeoutMs.invalid).toBeTrue();
    dialog.form.controls.relayTimeoutMs.setValue(1500);
    expect(dialog.form.controls.relayTimeoutMs.valid).toBeTrue();
  });

  it('edits a relayed payer and sends every field', () => {
    const payer = makePayer({
      relayEnabled: true, relayServiceUrl: 'https://crd.example.org/svc', relayTimeoutMs: 800,
      fallbackToLocalRules: false, forwardFhirAuthorization: true, relayClientId: 'segue-client',
    });
    const { dialog, submit } = create({ payer });
    expect(dialog.isEdit).toBeTrue();
    api.updatePayer.and.returnValue(of(payer));

    submit();

    expect(api.updatePayer).toHaveBeenCalledOnceWith('payer-1', jasmine.objectContaining({
      relayEnabled: true, relayServiceUrl: 'https://crd.example.org/svc', relayTimeoutMs: 800,
      fallbackToLocalRules: false, forwardFhirAuthorization: true, relayClientId: 'segue-client',
    }));
  });

  it('never sends the token for a payer that is not relayed', () => {
    const { dialog, submit } = create({ payer: makePayer({ forwardFhirAuthorization: true }) });
    api.updatePayer.and.returnValue(of(makePayer()));
    dialog.form.controls.relayEnabled.setValue(false);
    submit();
    expect(api.updatePayer.calls.mostRecent().args[1].forwardFhirAuthorization).toBeFalse();
  });

  it('shows the API reason when the save is refused', () => {
    const { dialog, el, submit } = create(null);
    api.createPayer.and.returnValue(throwError(() => Object.assign(
      new HttpErrorResponse({ status: 400 }), { message: "A payer named 'Acme' already exists." })));
    dialog.form.patchValue({ name: 'Acme', identifiers: ['ACME01'] });
    submit();
    expect(el.querySelector('[data-testid="cds-payer-dialog-error"]')?.textContent).toContain('already exists');
    expect(ref.close).not.toHaveBeenCalled();
  });

  it('will not save without the permission', () => {
    const { dialog, submit } = create({ payer: makePayer() }, ['cdshooks.view']);
    dialog.form.patchValue({ name: 'Other' });
    submit();
    expect(api.updatePayer).not.toHaveBeenCalled();
    expect(toast.warning).toHaveBeenCalled();
  });

  it('cancel goes through the unsaved-changes check', () => {
    const { el } = create(null);
    (el.querySelector('.btn-secondary') as HTMLButtonElement).click();
    expect(ref.attemptClose).toHaveBeenCalled();
  });
});
