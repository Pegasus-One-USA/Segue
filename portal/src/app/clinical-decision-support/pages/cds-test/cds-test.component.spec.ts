import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Subject, of, throwError } from 'rxjs';
import { CdsTestComponent } from './cds-test.component';
import { ToastService } from '../../../services/toast.service';
import { CdsApiService } from '../../services/cds-api.service';
import { CdsRequest, CdsServiceInstance, CdsTestResult } from '../../models/cds.models';
import { makeCdsApiSpy, makeService, makeTestResult, makeToastSpy } from '../../testing/cds-test-data';

describe('CdsTestComponent', () => {
  let api: ReturnType<typeof makeCdsApiSpy>;
  let toast: ReturnType<typeof makeToastSpy>;

  const SAMPLE: CdsRequest = { hook: 'order-sign', hookInstance: 'h-1', context: { userId: 'Practitioner/example' } };
  const RENAL = makeService();
  const OFF = makeService({ id: 'svc-2', title: 'Allergy', isEnabled: false });

  function create(services: CdsServiceInstance[] = [RENAL, OFF], query: Record<string, string> = {}) {
    api = makeCdsApiSpy();
    api.getServices.and.returnValue(of(services));
    api.getSampleRequest.and.returnValue(of(SAMPLE));
    toast = makeToastSpy();
    TestBed.configureTestingModule({
      imports: [CdsTestComponent],
      providers: [
        provideNoopAnimations(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) } } },
        { provide: CdsApiService, useValue: api },
        { provide: ToastService, useValue: toast },
      ],
    });
    const fixture = TestBed.createComponent(CdsTestComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const q = <T extends Element>(id: string) => el.querySelector(`[data-testid="${id}"]`) as T;
    const select = (id: string) => {
      const s = q<HTMLSelectElement>('cds-test-service');
      s.value = id;
      s.dispatchEvent(new Event('change'));
      fixture.detectChanges();
    };
    const setRequest = (text: string) => {
      const t = q<HTMLTextAreaElement>('cds-test-request');
      t.value = text;
      t.dispatchEvent(new Event('input'));
      fixture.detectChanges();
    };
    const run = () => {
      q<HTMLButtonElement>('cds-test-run').click();
      fixture.detectChanges();
    };
    return { fixture, page: fixture.componentInstance, el, q, select, setRequest, run };
  }

  it('says so when there is nothing to test', () => {
    const { el } = create([]);
    expect(el.textContent).toContain('There are no services to test yet.');
  });

  it('starts with nothing picked: the editor and Run are disabled, and the privacy notice shows', () => {
    const { el, q } = create();

    expect(q<HTMLTextAreaElement>('cds-test-request').disabled).toBeTrue();
    expect(q<HTMLButtonElement>('cds-test-run').disabled).toBeTrue();
    expect(el.textContent).toContain('It is not stored and is not written to the activity log.');
    expect(api.getSampleRequest).not.toHaveBeenCalled();
  });

  it('pre-selects ?service= and loads its sample request', () => {
    const { q, page } = create([RENAL, OFF], { service: 'svc-1' });

    expect(page.selectedId()).toBe('svc-1');
    expect(api.getSampleRequest).toHaveBeenCalledWith('svc-1');
    expect(JSON.parse(q<HTMLTextAreaElement>('cds-test-request').value)).toEqual(SAMPLE);
  });

  it('ignores an unknown ?service=', () => {
    const { page } = create([RENAL], { service: 'nope' });
    expect(page.selectedId()).toBe('');
  });

  it('notes that a service that is off can still be tested', () => {
    const { el, select } = create();
    select('svc-2');
    expect(el.textContent).toContain('off for EHRs, but you can still test it');
  });

  it('runs the edited request and renders the cards like an EHR', () => {
    const { el, select, setRequest, run } = create();
    api.testService.and.returnValue(of(makeTestResult()));
    select('svc-1');
    const edited = { ...SAMPLE, prefetch: { patient: { resourceType: 'Patient' } } };
    setRequest(JSON.stringify(edited));

    run();

    expect(api.testService).toHaveBeenCalledWith('svc-1', edited);
    const cards = el.querySelectorAll('[data-testid="cds-card"]');
    expect(cards.length).toBe(1);
    expect(cards[0].classList).toContain('card--warning');
    expect(cards[0].textContent).toContain('Reduce the dose: eGFR is below 30');
    expect(cards[0].querySelector('.card__links a')?.getAttribute('href')).toBe('https://guide.example.org');
  });

  it('shows card fixes as tickable buttons with a preview, and link buttons, sending nothing more', () => {
    const { el, select, run, fixture } = create();
    const result = makeTestResult();
    result.response!.cards[0] = {
      ...result.response!.cards[0],
      selectionBehavior: 'any',
      suggestions: [
        { label: 'Remove the draft', uuid: 's1', actions: [{ type: 'delete', resourceId: 'MedicationRequest/med-1' }] },
        {
          label: 'Order the renal dose', uuid: 's2', isRecommended: true,
          actions: [{
            type: 'create',
            resource: {
              resourceType: 'MedicationRequest', status: 'draft',
              medicationCodeableConcept: { coding: [{ system: 'urn:com.epic.cdshooks.action.code.system.preference-list-item', code: '12345', display: 'Renal dose' }] },
            },
          }],
        },
      ],
      links: [
        { label: 'Dosing guide', url: 'https://guide.example.org', type: 'absolute' },
        { label: 'Calculator', url: 'https://app.example.org/launch', type: 'smart', autolaunchable: true },
      ],
    };
    api.testService.and.returnValue(of(result));
    select('svc-1');
    run();

    const card = el.querySelector('[data-testid="cds-card"]')!;
    const fixes = Array.from(card.querySelectorAll('[data-testid="cds-suggestion"]')) as HTMLButtonElement[];
    expect(fixes.map(f => f.getAttribute('aria-pressed'))).toEqual(['false', 'true']);
    expect(card.querySelectorAll('[data-testid="cds-suggestion-preview"]').length).toBe(1);
    expect(card.textContent).toContain('Renal dose');
    expect(el.textContent).toContain('2 fixes offered');

    fixes[0].click();
    fixture.detectChanges();
    expect(card.textContent).toContain('MedicationRequest/med-1');
    expect(api.testService).toHaveBeenCalledTimes(1);

    const links = Array.from(card.querySelectorAll('[data-testid="cds-card-link"]'));
    expect(links.map(a => a.getAttribute('href'))).toEqual(['https://guide.example.org', 'https://app.example.org/launch']);
    expect(card.textContent).toContain('may open on its own');
  });

  it('rejects text that is not a JSON object without calling the API', () => {
    const { el, select, setRequest, run } = create();
    select('svc-1');

    setRequest('{ "hook": ');
    run();
    expect(el.textContent).toContain('This is not valid JSON.');

    setRequest('[1, 2]');
    run();
    expect(el.textContent).toContain('must be a JSON object');

    setRequest('   ');
    run();
    expect(el.textContent).toContain('Enter a request, or load the sample.');
    expect(api.testService).not.toHaveBeenCalled();
  });

  it('shows the backend message when the run fails', () => {
    const { el, select, run } = create();
    api.testService.and.returnValue(throwError(() => Object.assign(
      new HttpErrorResponse({ status: 400 }), { message: 'hook does not match the service.' })));
    select('svc-1');

    run();

    expect(el.querySelector('.response .cds-notice--error')?.textContent).toContain('hook does not match the service.');
    expect(el.querySelector('[data-testid="cds-card"]')).toBeNull();
  });

  it('drops a run still in flight when another service is picked', () => {
    const { page, select, run, fixture, el } = create();
    const late = new Subject<CdsTestResult>();
    api.testService.and.returnValue(late);
    select('svc-1');
    run();
    expect(page.running()).toBeTrue();

    select('svc-2');
    late.next(makeTestResult());
    fixture.detectChanges();

    expect(page.result()).toBeNull();
    expect(page.running()).toBeFalse();
    expect(el.querySelector('[data-testid="cds-card"]')).toBeNull();
  });

  it('clears the request and the result', () => {
    const { page, select, run, fixture } = create();
    api.testService.and.returnValue(of(makeTestResult()));
    select('svc-1');
    run();

    page.clear();
    fixture.detectChanges();

    expect(page.requestText()).toBe('');
    expect(page.result()).toBeNull();
  });

  it('shows an error toast when the services cannot load', () => {
    api = makeCdsApiSpy();
    TestBed.configureTestingModule({
      imports: [CdsTestComponent],
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({}) } } },
        { provide: CdsApiService, useValue: api },
        { provide: ToastService, useValue: (toast = makeToastSpy()) },
      ],
    });
    api.getServices.and.returnValue(throwError(() => new Error('down')));
    TestBed.createComponent(CdsTestComponent).detectChanges();
    expect(toast.error).toHaveBeenCalledWith('Could not load CDS services.');
  });
});
