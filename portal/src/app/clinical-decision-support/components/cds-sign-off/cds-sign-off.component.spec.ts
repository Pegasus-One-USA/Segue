import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';
import { CdsSignOffComponent } from './cds-sign-off.component';
import { CdsApiService } from '../../services/cds-api.service';
import { CdsServiceInstance } from '../../models/cds.models';
import { DialogService } from '../../../core/services/dialog.service';
import { ConfirmDialogComponent } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';
import { makeCdsApiSpy, makeDialogSpy, makeService, makeToastSpy } from '../../testing/cds-test-data';

describe('CdsSignOffComponent', () => {
  let api: jasmine.SpyObj<CdsApiService>;
  let dialog: { open: jasmine.Spy };
  let toast: ReturnType<typeof makeToastSpy>;

  function render(
    service: CdsServiceInstance,
    opts: { canEdit?: boolean; unsaved?: boolean; answer?: unknown } = {},
  ): ComponentFixture<CdsSignOffComponent> {
    api = makeCdsApiSpy();
    dialog = makeDialogSpy(opts.answer ?? true);
    toast = makeToastSpy();
    TestBed.configureTestingModule({
      imports: [CdsSignOffComponent],
      providers: [
        provideNoopAnimations(),
        { provide: CdsApiService, useValue: api },
        { provide: DialogService, useValue: dialog },
        { provide: ToastService, useValue: toast },
      ],
    });
    const fixture = TestBed.createComponent(CdsSignOffComponent);
    fixture.componentRef.setInput('service', service);
    fixture.componentRef.setInput('canEdit', opts.canEdit ?? true);
    fixture.componentRef.setInput('hasUnsavedChanges', opts.unsaved ?? false);
    fixture.detectChanges();
    return fixture;
  }

  function el(fixture: ComponentFixture<CdsSignOffComponent>): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function type(fixture: ComponentFixture<CdsSignOffComponent>, testId: string, value: string): void {
    const input = el(fixture).querySelector<HTMLInputElement>(`[data-testid="${testId}"]`)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function click(fixture: ComponentFixture<CdsSignOffComponent>, testId: string): void {
    el(fixture).querySelector<HTMLButtonElement>(`[data-testid="${testId}"]`)!.click();
    fixture.detectChanges();
  }

  it('shows "Not clinically reviewed" and the form when there is no sign-off', () => {
    const fixture = render(makeService());

    expect(el(fixture).querySelector('[data-testid="cds-sign-off-badge"]')?.textContent).toContain('Not clinically reviewed');
    expect(el(fixture).textContent).toContain('sample rules shipped with Segue');
    expect(el(fixture).querySelector('[data-testid="cds-sign-off-record"]')).not.toBeNull();
  });

  it('needs a name before asking anything', () => {
    const fixture = render(makeService());

    click(fixture, 'cds-sign-off-record');

    expect(dialog.open).not.toHaveBeenCalled();
    expect(el(fixture).querySelector('.cds-field__error')?.textContent).toContain('reviewing clinician');
  });

  it('asks first, then records the sign-off and emits the updated service', () => {
    const fixture = render(makeService());
    const updated = makeService({
      isClinicallySignedOff: true, signedOffBy: 'Dr A. Patel', signedOffAtUtc: '2026-10-15T14:00:00Z', signOffNote: 'P&T',
    });
    api.signOffService.and.returnValue(of(updated));
    const emitted: CdsServiceInstance[] = [];
    fixture.componentInstance.changed.subscribe(s => emitted.push(s));

    type(fixture, 'cds-sign-off-name', '  Dr A. Patel ');
    type(fixture, 'cds-sign-off-note', 'P&T');
    click(fixture, 'cds-sign-off-record');

    expect(dialog.open).toHaveBeenCalledWith(ConfirmDialogComponent, jasmine.objectContaining({
      data: jasmine.objectContaining({ title: 'Record clinical sign-off?', confirmLabel: 'Record sign-off' }),
    }));
    expect(api.signOffService).toHaveBeenCalledWith('svc-1', { signedOffBy: 'Dr A. Patel', note: 'P&T', contentVersion: 'v1' });
    expect(emitted).toEqual([updated]);
    expect(toast.success).toHaveBeenCalled();
  });

  it('does nothing when the question is cancelled', () => {
    const fixture = render(makeService(), { answer: false });

    type(fixture, 'cds-sign-off-name', 'Dr A');
    click(fixture, 'cds-sign-off-record');

    expect(api.signOffService).not.toHaveBeenCalled();
  });

  it('waits for unsaved changes to be saved first', () => {
    const fixture = render(makeService(), { unsaved: true });

    expect(el(fixture).querySelector('[data-testid="cds-sign-off-unsaved"]')).not.toBeNull();
    expect(el(fixture).querySelector<HTMLButtonElement>('[data-testid="cds-sign-off-record"]')!.disabled).toBeTrue();
  });

  it('shows the sign-off and removes it after a danger question', () => {
    const signed = makeService({
      isClinicallySignedOff: true, signedOffBy: 'Dr A. Patel', signedOffAtUtc: '2026-10-15T14:00:00Z', signOffNote: 'P&T review',
    });
    const fixture = render(signed);
    api.clearSignOff.and.returnValue(of(makeService()));

    expect(el(fixture).querySelector('[data-testid="cds-sign-off-details"]')?.textContent).toContain('P&T review');
    expect(el(fixture).querySelector('[data-testid="cds-sign-off-name"]')).toBeNull();

    click(fixture, 'cds-sign-off-remove');

    expect(dialog.open).toHaveBeenCalledWith(ConfirmDialogComponent, jasmine.objectContaining({
      data: jasmine.objectContaining({ danger: true, confirmLabel: 'Remove sign-off' }),
    }));
    expect(api.clearSignOff).toHaveBeenCalledWith('svc-1');
  });

  it('is read-only without edit permission', () => {
    const fixture = render(makeService(), { canEdit: false });

    expect(el(fixture).querySelector('[data-testid="cds-sign-off-record"]')).toBeNull();
    expect(el(fixture).querySelector('[data-testid="cds-sign-off-name"]')).toBeNull();
  });

  it('shows the API message when the save fails', () => {
    const fixture = render(makeService());
    api.signOffService.and.returnValue(throwError(() => ({ message: 'Reviewer name is required.' })));

    type(fixture, 'cds-sign-off-name', 'Dr A');
    click(fixture, 'cds-sign-off-record');

    expect(el(fixture).querySelector('.cds-notice--error')?.textContent).toContain('Reviewer name is required.');
  });

  it('explains a 409: the service changed after it was opened', () => {
    const fixture = render(makeService());
    api.signOffService.and.returnValue(throwError(() => ({ status: 409, message: 'Conflict' })));

    type(fixture, 'cds-sign-off-name', 'Dr A');
    click(fixture, 'cds-sign-off-record');

    expect(el(fixture).querySelector('.cds-notice--error')?.textContent).toContain('Someone changed this service after you opened it');
  });
});
