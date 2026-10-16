import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { CdsTrustedIssuerDialogComponent, CdsTrustedIssuerDialogData } from './cds-trusted-issuer-dialog.component';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';
import { AuthStore } from '../../../auth/store/auth.store';
import { ToastService } from '../../../services/toast.service';
import { CdsApiService } from '../../services/cds-api.service';
import { CDS_ALL_PERMISSIONS, makeCdsApiSpy, makeIssuer, makeToastSpy, signInWith } from '../../testing/cds-test-data';

describe('CdsTrustedIssuerDialogComponent', () => {
  let api: ReturnType<typeof makeCdsApiSpy>;
  let ref: { close: jasmine.Spy; attemptClose: jasmine.Spy };
  let toast: ReturnType<typeof makeToastSpy>;

  function create(data: CdsTrustedIssuerDialogData | null, permissions = CDS_ALL_PERMISSIONS) {
    api = makeCdsApiSpy();
    toast = makeToastSpy();
    ref = { close: jasmine.createSpy('close'), attemptClose: jasmine.createSpy('attemptClose') };
    TestBed.configureTestingModule({
      imports: [CdsTrustedIssuerDialogComponent],
      providers: [
        { provide: CdsApiService, useValue: api },
        { provide: ToastService, useValue: toast },
        { provide: DialogRef, useValue: ref },
        { provide: DIALOG_DATA, useValue: data },
      ],
    });
    signInWith(TestBed.inject(AuthStore), permissions);
    const fixture = TestBed.createComponent(CdsTrustedIssuerDialogComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const submit = () => {
      (el.querySelector('[data-testid="cds-issuer-dialog-save"]') as HTMLButtonElement).click();
      fixture.detectChanges();
    };
    return { fixture, dialog: fixture.componentInstance, el, submit };
  }

  it('starts blank and refuses to save without a way to get keys', () => {
    const { dialog, el, submit } = create(null);
    dialog.form.patchValue({ name: 'Epic', vendor: 'Epic', issuer: 'https://ehr.example.org' });

    submit();

    expect(api.createTrustedIssuer).not.toHaveBeenCalled();
    expect(el.textContent).toContain('Enter a JWKS URL or paste the keys.');
  });

  it('pre-fills the public sandbox values and creates it', () => {
    const { dialog, submit } = create({ sandbox: true });
    expect(dialog.form.getRawValue()).toEqual(jasmine.objectContaining({
      name: 'CDS Hooks sandbox', vendor: 'Other', issuer: 'https://sandbox.cds-hooks.org',
      jwksUrl: 'https://sandbox.cds-hooks.org/.well-known/jwks.json',
    }));
    const saved = makeIssuer({ name: 'CDS Hooks sandbox' });
    api.createTrustedIssuer.and.returnValue(of(saved));

    submit();

    expect(api.createTrustedIssuer).toHaveBeenCalledWith({
      name: 'CDS Hooks sandbox', vendor: 'Other', issuer: 'https://sandbox.cds-hooks.org',
      jwksUrl: 'https://sandbox.cds-hooks.org/.well-known/jwks.json', jwksJson: null, audience: null, isEnabled: true,
      expectedSubject: null, fhirBaseUrl: null, allowCallback: true,
    });
    expect(ref.close).toHaveBeenCalledWith(saved);
  });

  it('accepts pasted keys instead of a URL and rejects a non-JWKS paste', () => {
    const { dialog, el, submit } = create(null);
    dialog.form.patchValue({ name: 'X', vendor: 'Other', issuer: 'x', jwksJson: '{"keys":[]}' });
    submit();
    expect(api.createTrustedIssuer).not.toHaveBeenCalled();
    expect(el.textContent).toContain('"keys" list that has at least one key');

    api.createTrustedIssuer.and.returnValue(of(makeIssuer()));
    dialog.form.patchValue({ jwksJson: '{"keys":[{"kty":"EC"}]}' });
    submit();
    expect(api.createTrustedIssuer).toHaveBeenCalledWith(jasmine.objectContaining({ jwksUrl: null, jwksJson: '{"keys":[{"kty":"EC"}]}' }));
  });

  it('edits an existing issuer through the update endpoint', () => {
    const issuer = makeIssuer({ audience: 'https://aud.example.org' });
    const { dialog, el, submit } = create({ issuer });
    expect(el.querySelector('h2')?.textContent).toContain('Edit trusted issuer');
    expect(dialog.form.controls.audience.value).toBe('https://aud.example.org');
    api.updateTrustedIssuer.and.returnValue(of(issuer));

    dialog.form.patchValue({ name: '  Renamed  ' });
    submit();

    expect(api.updateTrustedIssuer).toHaveBeenCalledWith('iss-1', jasmine.objectContaining({ name: 'Renamed' }));
  });

  it('shows the backend message when the save fails and stays open', () => {
    const { dialog, el, submit } = create({ sandbox: true });
    // The app's error interceptor puts the backend's message on HttpErrorResponse.message.
    api.createTrustedIssuer.and.returnValue(throwError(() => Object.assign(
      new HttpErrorResponse({ status: 400 }), { message: 'An issuer with this iss already exists.' })));

    submit();

    expect(el.querySelector('[data-testid="cds-issuer-dialog-error"]')?.textContent).toContain('already exists');
    expect(ref.close).not.toHaveBeenCalled();
    expect(dialog.saving()).toBeFalse();
  });

  it('refuses to save without the matching permission', () => {
    const { submit } = create({ sandbox: true }, ['cdshooks.view']);
    submit();
    expect(api.createTrustedIssuer).not.toHaveBeenCalled();
    expect(toast.warning).toHaveBeenCalled();
  });

  describe('Epic fields', () => {
    const type = (fixture: { detectChanges(): void }, el: HTMLElement, sel: string, value: string) => {
      const input = el.querySelector(sel) as HTMLInputElement;
      input.value = value;
      input.dispatchEvent(new Event('input'));
      input.dispatchEvent(new Event('blur'));
      fixture.detectChanges();
    };

    it('shows the client id, FHIR server and callback fields, with the jku note, defaulting to callback on', () => {
      const { el, dialog } = create(null);
      expect(el.querySelector('#cds-issuer-sub')).not.toBeNull();
      expect(el.querySelector('#cds-issuer-fhir')).not.toBeNull();
      expect((el.querySelector('#cds-issuer-callback') as HTMLInputElement).checked).toBeTrue();
      expect(el.textContent).toContain('never fetches keys from any other address');
      expect(dialog.form.controls.allowCallback.value).toBeTrue();
    });

    it('uses the issuer being typed as the FHIR server placeholder', () => {
      const { el, fixture } = create(null);
      type(fixture, el, '#cds-issuer-iss', 'https://ehr.example.org/api/FHIR/R4');
      expect((el.querySelector('#cds-issuer-fhir') as HTMLInputElement).placeholder).toBe('https://ehr.example.org/api/FHIR/R4');
    });

    it('sends the client id, the FHIR server without a trailing slash, and the callback switch', () => {
      const { dialog, el, fixture, submit } = create({ sandbox: true });
      api.createTrustedIssuer.and.returnValue(of(makeIssuer()));
      type(fixture, el, '#cds-issuer-sub', '  client-123  ');
      type(fixture, el, '#cds-issuer-fhir', 'https://ehr.example.org/api/FHIR/R4//');
      (el.querySelector('#cds-issuer-callback') as HTMLInputElement).click();
      fixture.detectChanges();

      submit();

      expect(dialog.form.controls.allowCallback.value).toBeFalse();
      expect(api.createTrustedIssuer).toHaveBeenCalledWith(jasmine.objectContaining({
        expectedSubject: 'client-123', fhirBaseUrl: 'https://ehr.example.org/api/FHIR/R4', allowCallback: false,
      }));
    });

    it('refuses a FHIR server with a query string or that is not a web address', () => {
      const { dialog, el, fixture, submit } = create({ sandbox: true });
      type(fixture, el, '#cds-issuer-fhir', 'https://ehr.example.org/fhir?x=1');

      expect(el.textContent).toContain('Enter a full web address starting with https:// and without ? or #.');
      submit();
      expect(api.createTrustedIssuer).not.toHaveBeenCalled();

      type(fixture, el, '#cds-issuer-fhir', 'ftp://ehr.example.org/fhir');
      expect(dialog.form.controls.fhirBaseUrl.hasError('fhirBaseUrl')).toBeTrue();
    });

    it('accepts an http FHIR server (a dev server; the API calls it only when insecure callbacks are allowed)', () => {
      const { dialog, el, fixture } = create({ sandbox: true });
      type(fixture, el, '#cds-issuer-fhir', 'http://hapi:8080/fhir');

      expect(dialog.form.controls.fhirBaseUrl.valid).toBeTrue();
    });

    it('pre-fills the Epic fields when editing', () => {
      const issuer = makeIssuer({ expectedSubject: 'client-9', fhirBaseUrl: 'https://fhir.example.org', allowCallback: false });
      const { el, submit } = create({ issuer });
      expect((el.querySelector('#cds-issuer-sub') as HTMLInputElement).value).toBe('client-9');
      expect((el.querySelector('#cds-issuer-fhir') as HTMLInputElement).value).toBe('https://fhir.example.org');
      expect((el.querySelector('#cds-issuer-callback') as HTMLInputElement).checked).toBeFalse();

      api.updateTrustedIssuer.and.returnValue(of(issuer));
      submit();
      expect(api.updateTrustedIssuer).toHaveBeenCalledWith('iss-1', jasmine.objectContaining({
        expectedSubject: 'client-9', fhirBaseUrl: 'https://fhir.example.org', allowCallback: false,
      }));
    });
  });

  it('asks before closing via Cancel', () => {
    const { el } = create(null);
    (el.querySelector('.btn-secondary') as HTMLButtonElement).click();
    expect(ref.attemptClose).toHaveBeenCalled();
  });
});
