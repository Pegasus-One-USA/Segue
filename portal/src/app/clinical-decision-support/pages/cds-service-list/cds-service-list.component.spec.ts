import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Router, provideRouter } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { CdsServiceListComponent } from './cds-service-list.component';
import { AuthStore } from '../../../auth/store/auth.store';
import { DialogService } from '../../../core/services/dialog.service';
import { ToastService } from '../../../services/toast.service';
import { CdsApiService } from '../../services/cds-api.service';
import { CdsServiceInstance } from '../../models/cds.models';
import {
  CDS_ALL_PERMISSIONS, makeCdsApiSpy, makeDialogSpy, makeHandler, makeService, makeToastSpy, signInWith,
} from '../../testing/cds-test-data';

describe('CdsServiceListComponent', () => {
  let api: ReturnType<typeof makeCdsApiSpy>;
  let toast: ReturnType<typeof makeToastSpy>;
  let dialog: ReturnType<typeof makeDialogSpy>;

  const RENAL = makeService();
  const GAPS = makeService({
    id: 'svc-2', serviceId: 'care-gaps', title: 'Care gaps', handlerKey: 'care-gap', handlerDisplayName: null,
    hook: 'patient-view', isEnabled: false,
  });

  function create(services: CdsServiceInstance[], permissions = CDS_ALL_PERMISSIONS, dialogResult: unknown = undefined) {
    api = makeCdsApiSpy();
    api.getServices.and.returnValue(of(services));
    toast = makeToastSpy();
    dialog = makeDialogSpy(dialogResult);
    TestBed.configureTestingModule({
      imports: [CdsServiceListComponent],
      providers: [
        provideNoopAnimations(),
        provideRouter([]),
        { provide: CdsApiService, useValue: api },
        { provide: ToastService, useValue: toast },
        { provide: DialogService, useValue: dialog },
      ],
    });
    signInWith(TestBed.inject(AuthStore), permissions);
    const fixture = TestBed.createComponent(CdsServiceListComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return {
      fixture, page: fixture.componentInstance, el,
      rows: () => Array.from(el.querySelectorAll('[data-testid="cds-service-row"]')),
      toggles: () => Array.from(el.querySelectorAll('[data-testid="cds-service-toggle"] button[role="switch"]')) as HTMLButtonElement[],
    };
  }

  it('renders one row per service with its check, hook and state', () => {
    const { el, rows, toggles } = create([RENAL, GAPS]);

    expect(rows().length).toBe(2);
    expect(rows()[0].textContent).toContain('renal-dose-check');
    expect(rows()[0].textContent).toContain('Order signed');
    expect(rows()[1].textContent).toContain('care-gap (unavailable)');
    expect(toggles()[0].getAttribute('aria-checked')).toBe('true');
    expect(toggles()[1].getAttribute('aria-checked')).toBe('false');
    expect(el.textContent).toContain('2 of 2 services');
  });

  it('badges only the services whose fixes change EHR drafts', () => {
    const { rows } = create([makeService({ fixesEnabled: true }), GAPS]);
    expect(rows()[0].querySelector('[data-testid="cds-fixes-badge"]')?.textContent).toContain('Changes EHR drafts');
    expect(rows()[1].querySelector('[data-testid="cds-fixes-badge"]')).toBeNull();
  });

  it('sends the Epic options back unchanged when the switch changes', () => {
    const fixes = { enabled: true, fixTypes: [{ key: 'remove-draft', enabled: true, mappings: [] }], recommendedFixType: 'remove-draft' };
    const reasons = [{ code: 'pt-refused', display: 'Patient refused' }];
    const links = [{ label: 'Guide', url: 'https://guide.example.org', type: 'absolute' as const }];
    const service = makeService({
      fetchMissingData: false, fixes, fixesEnabled: true, overrideReasons: reasons, overrideReasonsAreDefault: false, links,
    });
    const { fixture, toggles } = create([service]);
    api.updateService.and.returnValue(of({ ...service, isEnabled: false }));

    toggles()[0].click();
    fixture.detectChanges();

    expect(api.updateService).toHaveBeenCalledWith('svc-1', jasmine.objectContaining({
      isEnabled: false, fetchMissingData: false, fixes, overrideReasons: reasons, links,
    }));
    expect(dialog.open).not.toHaveBeenCalled();
  });

  it('shows the empty state when there are no services', () => {
    expect(create([]).el.textContent).toContain('No services yet');
  });

  it('filters by name, id or check', () => {
    const { fixture, el, rows } = create([RENAL, GAPS]);
    const search = el.querySelector('[data-testid="cds-service-search"]') as HTMLInputElement;
    search.value = 'care-';
    search.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    expect(rows().length).toBe(1);
    expect(rows()[0].textContent).toContain('Care gaps');
  });

  it('hides Add and disables the switches for a view-only role', () => {
    const { el, toggles } = create([RENAL], ['cdshooks.view']);

    expect(el.querySelector('[data-testid="cds-service-add"]')).toBeNull();
    expect(toggles()[0].disabled).toBeTrue();
  });

  it('opens the check picker and goes to the new-service page with the picked check', () => {
    const { el, fixture } = create([], CDS_ALL_PERMISSIONS, 'renal-dose');
    api.getHandlers.and.returnValue(of([makeHandler()]));
    const navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);

    (el.querySelector('[data-testid="cds-service-add"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(dialog.open.calls.mostRecent().args[1].data.handlers.length).toBe(1);
    expect(navigate).toHaveBeenCalledWith(['/clinical-decision-support/services/new'], { queryParams: { handler: 'renal-dose' } });
  });

  it('saves the whole service with isEnabled flipped when the switch changes', () => {
    const { fixture, toggles, page } = create([RENAL]);
    api.updateService.and.returnValue(of({ ...RENAL, isEnabled: false }));

    toggles()[0].click();
    fixture.detectChanges();

    // The stored settings ({} = all defaults), never the resolved ones, so defaults are not frozen in.
    expect(api.updateService).toHaveBeenCalledWith('svc-1', {
      serviceId: RENAL.serviceId, title: RENAL.title, handlerKey: RENAL.handlerKey, hook: RENAL.hook,
      description: RENAL.description, isEnabled: false, settings: {}, cardSourceLabel: RENAL.cardSourceLabel,
      cardSourceUrl: RENAL.cardSourceUrl, allowedIssuerIds: RENAL.allowedIssuerIds,
      fetchMissingData: true, fixes: null, overrideReasons: null, links: null,
    });
    expect(page.services()[0].isEnabled).toBeFalse();
    expect(toast.success).toHaveBeenCalledWith('"Renal dose check" is now off.');
  });

  it('flips the switch back and shows the backend message when the save fails', () => {
    const { fixture, toggles, page } = create([RENAL]);
    api.updateService.and.returnValue(throwError(() => Object.assign(
      new HttpErrorResponse({ status: 400 }), { message: 'Unknown check "renal-dose".' })));

    toggles()[0].click();
    fixture.detectChanges();

    expect(toast.error).toHaveBeenCalledWith('Unknown check "renal-dose".');
    expect(page.services()[0].isEnabled).toBeTrue();
    expect(toggles()[0].getAttribute('aria-checked')).toBe('true');
    expect(page.isToggling('svc-1')).toBeFalse();
  });

  it('deletes after confirmation and reloads', () => {
    const { page } = create([RENAL], CDS_ALL_PERMISSIONS, true);
    api.deleteService.and.returnValue(of(undefined));

    page.confirmDelete(RENAL);

    expect(api.deleteService).toHaveBeenCalledWith('svc-1');
    expect(api.getServices).toHaveBeenCalledTimes(2);
  });

  it('does not delete without delete permission', () => {
    const { page } = create([RENAL], ['cdshooks.view', 'cdshooks.edit'], true);
    page.confirmDelete(RENAL);
    expect(dialog.open).not.toHaveBeenCalled();
    expect(api.deleteService).not.toHaveBeenCalled();
  });

  it('shows an error toast when the list cannot load', () => {
    const { page } = create([]);
    api.getServices.and.returnValue(throwError(() => new Error('down')));

    page.load();

    expect(toast.error).toHaveBeenCalledWith('Could not load CDS services.');
    expect(page.loading()).toBeFalse();
  });
});
