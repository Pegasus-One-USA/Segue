import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';
import { CdsConnectEhrComponent } from './cds-connect-ehr.component';
import { CdsTrustedIssuerDialogComponent } from '../../dialogs/cds-trusted-issuer-dialog/cds-trusted-issuer-dialog.component';
import { AuthStore } from '../../../auth/store/auth.store';
import { DialogService } from '../../../core/services/dialog.service';
import { ToastService } from '../../../services/toast.service';
import { CdsApiService } from '../../services/cds-api.service';
import {
  CDS_ALL_PERMISSIONS, makeCdsApiSpy, makeDialogSpy, makeDiscovery, makeIssuer, makeSetupSheet, makeToastSpy, signInWith,
} from '../../testing/cds-test-data';
import { ActivatedRoute, convertToParamMap } from '@angular/router';

describe('CdsConnectEhrComponent', () => {
  let api: ReturnType<typeof makeCdsApiSpy>;
  let toast: ReturnType<typeof makeToastSpy>;
  let dialog: ReturnType<typeof makeDialogSpy>;

  function create(
    permissions = CDS_ALL_PERMISSIONS,
    setup: (a: ReturnType<typeof makeCdsApiSpy>) => void = () => undefined,
    query: Record<string, string> | null = null,
  ) {
    api = makeCdsApiSpy();
    setup(api);
    toast = makeToastSpy();
    dialog = makeDialogSpy();
    TestBed.configureTestingModule({
      imports: [CdsConnectEhrComponent],
      providers: [
        provideNoopAnimations(),
        { provide: CdsApiService, useValue: api },
        { provide: ToastService, useValue: toast },
        { provide: DialogService, useValue: dialog },
        ...(query ? [{ provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) } } }] : []),
      ],
    });
    signInWith(TestBed.inject(AuthStore), permissions);
    const fixture = TestBed.createComponent(CdsConnectEhrComponent);
    fixture.detectChanges();
    return { fixture, page: fixture.componentInstance, el: fixture.nativeElement as HTMLElement };
  }

  it('shows the discovery URL, the issuers and the sandbox box', () => {
    const { el } = create(CDS_ALL_PERMISSIONS, a => a.getTrustedIssuers.and.returnValue(of([makeIssuer()])));

    expect(el.querySelector('[data-testid="cds-discovery-url"]')?.textContent).toBe('https://segue.example.org/cds-services');
    expect(el.querySelectorAll('[data-testid="cds-issuer-row"]').length).toBe(1);
    expect(el.querySelector('app-cds-sandbox-hint')?.textContent).toContain('https://segue.example.org/cds-services');
  });

  it('opens the issuer dialog pre-filled with the sandbox', () => {
    const { el, fixture } = create();

    (el.querySelector('[data-testid="cds-add-sandbox"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(dialog.open).toHaveBeenCalledWith(CdsTrustedIssuerDialogComponent, jasmine.objectContaining({ data: { sandbox: true } }));
  });

  it('shows "already trusted" once the sandbox issuer exists', () => {
    const { el } = create(CDS_ALL_PERMISSIONS, a =>
      a.getTrustedIssuers.and.returnValue(of([makeIssuer({ issuer: 'https://sandbox.cds-hooks.org' })])));

    expect(el.querySelector('[data-testid="cds-add-sandbox"]')).toBeNull();
    expect(el.textContent).toContain('Sandbox already trusted');
  });

  it('hides every add button from a view-only role', () => {
    const { el } = create(['cdshooks.view']);
    expect(el.querySelector('[data-testid="cds-add-sandbox"]')).toBeNull();
    expect(el.querySelector('[data-testid="cds-issuer-add"]')).toBeNull();
  });

  it('still shows the issuers when discovery info fails, with one toast', () => {
    const { el } = create(CDS_ALL_PERMISSIONS, a => {
      a.getDiscoveryInfo.and.returnValue(throwError(() => new Error('down')));
      a.getTrustedIssuers.and.returnValue(of([makeIssuer()]));
    });

    expect(el.querySelector('[data-testid="cds-discovery-url"]')).toBeNull();
    expect(el.querySelectorAll('[data-testid="cds-issuer-row"]').length).toBe(1);
    expect(toast.error).toHaveBeenCalledOnceWith('Some connection details could not be loaded.');
  });

  it('shows the Epic setup sheet for the first service, or the one named by ?service=', () => {
    const services = [
      { id: 'svc-1', serviceId: 'renal', title: 'Renal', hook: 'order-sign', isEnabled: true, serviceUrl: 'https://s/1', feedbackUrl: 'https://s/1/feedback' },
      { id: 'svc-2', serviceId: 'gaps', title: 'Gaps', hook: 'patient-view', isEnabled: true, serviceUrl: 'https://s/2', feedbackUrl: 'https://s/2/feedback' },
    ];
    const setup = (a: ReturnType<typeof makeCdsApiSpy>) => {
      a.getDiscoveryInfo.and.returnValue(of(makeDiscovery({ services })));
      a.getSetupSheet.and.returnValue(of(makeSetupSheet()));
    };

    const first = create(CDS_ALL_PERMISSIONS, setup);
    expect(first.el.querySelector('[data-testid="cds-epic-setup-sheet"]')).not.toBeNull();
    expect(api.getSetupSheet).toHaveBeenCalledOnceWith('svc-1');

    TestBed.resetTestingModule();
    create(CDS_ALL_PERMISSIONS, setup, { service: 'svc-2' });
    expect(api.getSetupSheet).toHaveBeenCalledOnceWith('svc-2');
  });

  it('has no setup sheet when discovery info fails', () => {
    const { el } = create(CDS_ALL_PERMISSIONS, a => a.getDiscoveryInfo.and.returnValue(throwError(() => new Error('down'))));
    expect(el.querySelector('[data-testid="cds-epic-setup-sheet"]')).toBeNull();
  });

  it('reloads after an issuer is saved', () => {
    const { el, fixture } = create();
    dialog.open.and.callFake(() => ({ afterClosed: () => of(makeIssuer()) }));
    api.getDiscoveryInfo.and.returnValue(of(makeDiscovery({ discoveryUrl: 'https://new/cds-services' })));

    (el.querySelector('[data-testid="cds-issuer-add"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(api.getTrustedIssuers).toHaveBeenCalledTimes(2);
    expect(el.querySelector('[data-testid="cds-discovery-url"]')?.textContent).toBe('https://new/cds-services');
  });
});
