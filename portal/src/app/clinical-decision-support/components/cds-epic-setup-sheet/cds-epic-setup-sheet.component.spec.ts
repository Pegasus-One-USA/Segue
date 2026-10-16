import { TestBed, fakeAsync, flushMicrotasks } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { CdsEpicSetupSheetComponent } from './cds-epic-setup-sheet.component';
import { ToastService } from '../../../services/toast.service';
import { CdsApiService } from '../../services/cds-api.service';
import { CdsDiscoveryInfoService } from '../../models/cds.models';
import { makeCdsApiSpy, makeSetupSheet, makeSetupSheetCrd, makeToastSpy } from '../../testing/cds-test-data';

describe('CdsEpicSetupSheetComponent', () => {
  let api: ReturnType<typeof makeCdsApiSpy>;
  let toast: ReturnType<typeof makeToastSpy>;

  function service(id: string, title: string, isEnabled = true): CdsDiscoveryInfoService {
    return {
      id, serviceId: title.toLowerCase().replace(/ /g, '-'), title, hook: 'order-sign', isEnabled,
      serviceUrl: `https://segue.example.org/cds-services/${id}`, feedbackUrl: `https://segue.example.org/cds-services/${id}/feedback`,
    };
  }

  const SERVICES = [service('svc-1', 'Renal dose check'), service('svc-2', 'Care gaps', false)];

  function create(services: CdsDiscoveryInfoService[] = SERVICES, initialServiceId: string | null = null) {
    api = makeCdsApiSpy();
    api.getSetupSheet.and.callFake((id: string) => of(makeSetupSheet({ serviceInstanceId: id, serviceId: id })));
    toast = makeToastSpy();
    TestBed.configureTestingModule({
      imports: [CdsEpicSetupSheetComponent],
      providers: [
        provideNoopAnimations(),
        provideRouter([]),
        { provide: CdsApiService, useValue: api },
        { provide: ToastService, useValue: toast },
      ],
    });
    const fixture = TestBed.createComponent(CdsEpicSetupSheetComponent);
    fixture.componentRef.setInput('services', services);
    fixture.componentRef.setInput('initialServiceId', initialServiceId);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return { fixture, sheet: fixture.componentInstance, el };
  }

  it('asks for a service first when there are none', () => {
    const { el } = create([]);
    expect(el.textContent).toContain('Add a service first.');
    expect(api.getSetupSheet).not.toHaveBeenCalled();
  });

  it('opens the first service and lists its endpoints, issuer, prefetch and reason codes', () => {
    const { el } = create();

    expect(api.getSetupSheet).toHaveBeenCalledOnceWith('svc-1');
    const text = el.textContent ?? '';
    expect(text).toContain('https://segue.example.org/cds-services/renal-dose-check/feedback');
    expect(text).toContain('https://ehr.example.org/jwks');
    expect(text).toContain('client-123');
    expect(text).toContain('Observation?patient={{context.patientId}}&code=33914-3');
    expect(text).toContain('Segue fetches it');
    expect(text).toContain('will-monitor');
    expect(text).toContain("Off. Cards from this service don't change anything in Epic.");
    expect(text).toContain('Contains settings only, no patient data.');
    const options = Array.from((el.querySelector('[data-testid="cds-setup-sheet-service"]') as HTMLSelectElement).options);
    expect(options.map(o => o.textContent?.trim())).toEqual(['Renal dose check', 'Care gaps (off)']);
  });

  it('shows the coverage requirements part only for a coverage requirements service', () => {
    const { el, fixture } = create();
    expect(el.querySelector('[data-testid="cds-setup-sheet-crd"]')).toBeNull();

    api.getSetupSheet.and.returnValue(of(makeSetupSheet({ crd: makeSetupSheetCrd() })));
    fixture.componentInstance.select('svc-2');
    fixture.detectChanges();
    const crd = el.querySelector('[data-testid="cds-setup-sheet-crd"]');
    expect(crd?.textContent).toContain('7. Coverage requirements (CRD)');
    expect(crd?.textContent).toContain('ServiceRequest.Update (Unsigned Order)');
    expect(crd?.textContent).toContain('Acme Health Plan');
  });

  it('shows whether the service is clinically reviewed', () => {
    const { el } = create();
    expect(el.querySelector('[data-testid="cds-setup-sheet-sign-off"]')?.textContent).toContain('Not clinically reviewed');
  });

  it('shows who reviewed the service and the note', () => {
    const { el, fixture } = create();
    api.getSetupSheet.and.returnValue(of(makeSetupSheet({
      isClinicallySignedOff: true, signedOffBy: 'Dr A. Patel', signedOffAtUtc: '2026-10-15T14:00:00Z', signOffNote: 'P&T review',
    })));
    (el.querySelector('[data-testid="cds-setup-sheet-service"]') as HTMLSelectElement).value = 'svc-2';
    el.querySelector('[data-testid="cds-setup-sheet-service"]')!.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    const cell = el.querySelector('[data-testid="cds-setup-sheet-sign-off"]')?.textContent ?? '';
    expect(cell).toContain('Reviewed by Dr A. Patel');
    expect(cell).toContain('P&T review');
  });

  it('opens the service named by ?service= first', () => {
    const { sheet } = create(SERVICES, 'svc-2');
    expect(api.getSetupSheet).toHaveBeenCalledOnceWith('svc-2');
    expect(sheet.selectedId()).toBe('svc-2');
  });

  it('falls back to the first service when the named one is unknown', () => {
    create(SERVICES, 'gone');
    expect(api.getSetupSheet).toHaveBeenCalledOnceWith('svc-1');
  });

  it('loads another service when picked', () => {
    const { el, fixture, sheet } = create();
    const select = el.querySelector('[data-testid="cds-setup-sheet-service"]') as HTMLSelectElement;
    select.value = 'svc-2';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(api.getSetupSheet).toHaveBeenCalledWith('svc-2');
    expect(sheet.sheet()?.serviceInstanceId).toBe('svc-2');
  });

  it('lists fixes, warnings and SMART links to allow-list', () => {
    const { fixture, el, sheet } = create();
    sheet.sheet.set(makeSetupSheet({
      fixesEnabled: true,
      warnings: ['Cds:PublicBaseUrl is not set.'],
      fixes: [
        { fixTypeKey: 'remove-draft', label: 'Remove the draft', actionType: 'delete', resourceType: 'MedicationRequest', trigger: null, system: null, code: null, display: null, isRecommended: false },
        { fixTypeKey: 'add-alternative', label: 'Order an alternative', actionType: 'create', resourceType: 'MedicationRequest', trigger: 'nsaid', triggerLabel: 'NSAIDs', system: 'urn:oid:2.16.840.1.113883.6.88', code: '198013', display: 'Naproxen', isRecommended: true },
      ],
      links: [
        { label: 'Guide', url: 'https://guide.example.org', type: 'absolute' },
        { label: 'Calculator', url: 'https://app.example.org/launch', type: 'smart', autolaunchable: true },
      ],
    }));
    fixture.detectChanges();

    const text = el.textContent ?? '';
    expect(text).toContain('Cds:PublicBaseUrl is not set.');
    expect(text).toContain('Removes');
    expect(text).toContain('198013');
    expect(text).toContain('Pre-selected');
    expect(text).toContain('NSAIDs');
    expect(text).not.toContain('nsaid');
    expect(text).toContain('Add each SMART app below to your SMART app allow-list');
    expect(text).toContain('SMART app (allow-list it)');
  });

  it('re-fetches the open sheet when the page reloads its services (e.g. after an issuer changed)', () => {
    const { fixture, sheet } = create();
    api.getSetupSheet.calls.reset();
    api.getSetupSheet.and.returnValue(of(makeSetupSheet({ serviceInstanceId: 'svc-1', serviceId: 'svc-1', warnings: ['fresh'] })));

    fixture.componentRef.setInput('services', [...SERVICES]);
    fixture.detectChanges();

    expect(api.getSetupSheet).toHaveBeenCalledOnceWith('svc-1');
    expect(sheet.selectedId()).toBe('svc-1');
    expect(sheet.sheet()?.warnings).toEqual(['fresh']);
  });

  it('points to the trusted issuers list above when no issuer can call the service', () => {
    const { fixture, el, sheet } = create();
    sheet.sheet.set(makeSetupSheet({ issuers: [] }));
    fixture.detectChanges();

    expect(el.textContent).toContain('Add one in Trusted issuers above.');
  });

  it('copies the whole sheet as JSON', fakeAsync(() => {
    const write = spyOn(navigator.clipboard, 'writeText').and.returnValue(Promise.resolve());
    const { el, sheet } = create();

    (el.querySelector('[aria-label="Copy setup sheet as JSON"]') as HTMLButtonElement).click();
    flushMicrotasks();

    expect(write).toHaveBeenCalledWith(sheet.json());
    expect(JSON.parse(write.calls.mostRecent().args[0])).toEqual(sheet.sheet()!);
  }));

  it('downloads the sheet as a JSON file named after the service', () => {
    const { el } = create();
    const createUrl = spyOn(URL, 'createObjectURL').and.returnValue('blob:sheet');
    const revoke = spyOn(URL, 'revokeObjectURL');
    let downloaded = '';
    let href = '';
    spyOn(HTMLAnchorElement.prototype, 'click').and.callFake(function (this: HTMLAnchorElement) {
      downloaded = this.download;
      href = this.href;
    });

    (el.querySelector('[data-testid="cds-setup-sheet-download"]') as HTMLButtonElement).click();

    expect(createUrl).toHaveBeenCalled();
    const blob = createUrl.calls.mostRecent().args[0] as Blob;
    expect(blob.type).toBe('application/json');
    expect(downloaded).toBe('epic-setup-svc-1.json');
    expect(href).toBe('blob:sheet');
    expect(revoke).toHaveBeenCalledWith('blob:sheet');
  });

  it('shows a toast when the download fails', () => {
    const { el } = create();
    spyOn(URL, 'createObjectURL').and.throwError('no blobs');
    (el.querySelector('[data-testid="cds-setup-sheet-download"]') as HTMLButtonElement).click();
    expect(toast.error).toHaveBeenCalledWith('Could not download the setup sheet.');
  });

  it('shows the API message and no copy/download buttons when the sheet cannot load', () => {
    api = makeCdsApiSpy();
    TestBed.configureTestingModule({
      imports: [CdsEpicSetupSheetComponent],
      providers: [provideNoopAnimations(), { provide: CdsApiService, useValue: api }, { provide: ToastService, useValue: makeToastSpy() }],
    });
    api.getSetupSheet.and.returnValue(throwError(() => Object.assign(
      new HttpErrorResponse({ status: 404 }), { message: 'The service no longer exists.' })));
    const fixture = TestBed.createComponent(CdsEpicSetupSheetComponent);
    fixture.componentRef.setInput('services', SERVICES);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.querySelector('[role="alert"]')?.textContent).toContain('The service no longer exists.');
    expect(el.querySelector('[data-testid="cds-setup-sheet-download"]')).toBeNull();
    expect(fixture.componentInstance.loading()).toBeFalse();
  });
});
