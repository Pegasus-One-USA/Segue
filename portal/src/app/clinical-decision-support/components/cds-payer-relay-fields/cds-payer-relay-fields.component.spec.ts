import { TestBed } from '@angular/core/testing';
import { FormControl } from '@angular/forms';
import { CdsPayerRelayControls, CdsPayerRelayFieldsComponent } from './cds-payer-relay-fields.component';
import { DialogService } from '../../../core/services/dialog.service';
import { ConfirmDialogComponent } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { makeDialogSpy } from '../../testing/cds-test-data';

describe('CdsPayerRelayFieldsComponent', () => {
  let dialog: ReturnType<typeof makeDialogSpy>;

  function controls(overrides: Partial<{ relayEnabled: boolean; forward: boolean }> = {}): CdsPayerRelayControls {
    return {
      relayEnabled: new FormControl(overrides.relayEnabled ?? true, { nonNullable: true }),
      relayServiceUrl: new FormControl('', { nonNullable: true }),
      relayTimeoutMs: new FormControl(1000, { nonNullable: true }),
      fallbackToLocalRules: new FormControl(true, { nonNullable: true }),
      relayClientId: new FormControl('', { nonNullable: true }),
      forwardFhirAuthorization: new FormControl(overrides.forward ?? false, { nonNullable: true }),
    };
  }

  function create(c: CdsPayerRelayControls, confirm: unknown = true) {
    dialog = makeDialogSpy(confirm);
    TestBed.configureTestingModule({
      imports: [CdsPayerRelayFieldsComponent],
      providers: [{ provide: DialogService, useValue: dialog }],
    });
    const fixture = TestBed.createComponent(CdsPayerRelayFieldsComponent);
    fixture.componentRef.setInput('controls', c);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const tokenBox = () => el.querySelector('[data-testid="cds-payer-relay-token"]') as HTMLInputElement;
    return { fixture, el, tokenBox };
  }

  it('shows only the switch while the relay is off', () => {
    const { el } = create(controls({ relayEnabled: false }));
    expect(el.querySelector('[data-testid="cds-payer-relay-enabled"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="cds-payer-relay-url"]')).toBeNull();
  });

  it('shows the relay fields with the token off and explained', () => {
    const { el, tokenBox } = create(controls());
    expect(el.querySelector('[data-testid="cds-payer-relay-url"]')).not.toBeNull();
    expect(tokenBox().checked).toBeFalse();
    expect(el.textContent).toContain('never a token');
  });

  it('asks before sending the token and turns it on only when confirmed', () => {
    const c = controls();
    const { fixture, el, tokenBox } = create(c, true);
    tokenBox().click();
    fixture.detectChanges();
    expect(dialog.open).toHaveBeenCalledWith(ConfirmDialogComponent, jasmine.objectContaining({
      data: jasmine.objectContaining({ title: "Send the EHR's access token?", danger: true }),
    }));
    expect(c.forwardFhirAuthorization.value).toBeTrue();
    expect(el.querySelector('[data-testid="cds-payer-relay-token-warning"]')).not.toBeNull();
  });

  it('keeps the token off when the confirmation is cancelled', () => {
    const c = controls();
    const { fixture, tokenBox } = create(c, false);
    tokenBox().click();
    fixture.detectChanges();
    expect(c.forwardFhirAuthorization.value).toBeFalse();
    expect(tokenBox().checked).toBeFalse();
  });

  it('turns the token off without asking', () => {
    const c = controls({ forward: true });
    const { tokenBox } = create(c);
    tokenBox().click();
    expect(dialog.open).not.toHaveBeenCalled();
    expect(c.forwardFhirAuthorization.value).toBeFalse();
    expect(c.forwardFhirAuthorization.dirty).toBeTrue();
  });

  it('shows the address error once the field is touched', () => {
    const c = controls();
    c.relayServiceUrl.setValidators(control => (control.value ? null : { required: true }));
    c.relayServiceUrl.updateValueAndValidity();
    c.relayServiceUrl.markAsTouched();
    const { el } = create(c);
    expect(el.textContent).toContain("Enter the payer's service address.");
  });
});
