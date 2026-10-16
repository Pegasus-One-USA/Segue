import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { Observable, of, throwError } from 'rxjs';
import { CdsServiceSetupComponent } from './cds-service-setup.component';
import { AuthStore } from '../../../auth/store/auth.store';
import { ToastService } from '../../../services/toast.service';
import { unsavedChangesGuard } from '../../../core/guards/unsaved-changes.guard';
import { HasUnsavedChanges } from '../../../core/guards/has-unsaved-changes';
import { UnsavedChangesPromptService } from '../../../core/services/unsaved-changes-prompt.service';
import { CdsApiService } from '../../services/cds-api.service';
import { CdsServiceInstance } from '../../models/cds.models';
import { DialogService } from '../../../core/services/dialog.service';
import { ConfirmDialogComponent } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import {
  CDS_ALL_PERMISSIONS, makeCdsApiSpy, makeCreateFixType, makeDeleteFixType, makeDialogSpy, makeHandler, makeIssuer, makeService,
  makeSetting, makeToastSpy, signInWith,
} from '../../testing/cds-test-data';

@Component({ standalone: true, template: 'list' })
class ListStubComponent {}

describe('CdsServiceSetupComponent', () => {
  let api: ReturnType<typeof makeCdsApiSpy>;
  let toast: ReturnType<typeof makeToastSpy>;
  /** Stands in for the "Discard changes?" prompt: answers Cancel (stay) whenever the page has edits. */
  let prompt: { confirmLeavePage: jasmine.Spy };

  const RENAL = makeHandler();
  const CARE_GAP = makeHandler({
    key: 'care-gap', displayName: 'Care gaps', supportedHooks: ['patient-view'],
    settings: [makeSetting({ key: 'hba1cMonths', label: 'HbA1c interval (months)', defaultValue: 6 })],
    prefetchByHook: { 'patient-view': { patient: 'Patient/{{context.patientId}}' } },
  });

  /** A check that offers one-click fixes. */
  const ALLERGY = makeHandler({
    key: 'drug-allergy', displayName: 'Drug allergy', settings: [],
    fixTypes: [makeDeleteFixType(), makeCreateFixType()],
  });

  /** Stands in for the confirm question; answers `dialogAnswer`. */
  let dialog: ReturnType<typeof makeDialogSpy>;
  let dialogAnswer: unknown = true;

  async function open(url: string, permissions = CDS_ALL_PERMISSIONS, instance: CdsServiceInstance | 'not-found' = makeService()) {
    api = makeCdsApiSpy();
    api.getHandlers.and.returnValue(of([RENAL, CARE_GAP, ALLERGY]));
    dialog = makeDialogSpy(dialogAnswer);
    api.getTrustedIssuers.and.returnValue(of([makeIssuer()]));
    api.getService.and.returnValue(instance === 'not-found'
      ? throwError(() => new HttpErrorResponse({ status: 404 }))
      : of(instance));
    toast = makeToastSpy();
    prompt = {
      confirmLeavePage: jasmine.createSpy('confirmLeavePage')
        .and.callFake((page: HasUnsavedChanges): Observable<boolean> => of(!page.hasUnsavedChanges())),
    };
    TestBed.configureTestingModule({
      providers: [
        provideNoopAnimations(),
        provideRouter([
          { path: 'clinical-decision-support/services', component: ListStubComponent },
          { path: 'clinical-decision-support/services/new', component: CdsServiceSetupComponent, canDeactivate: [unsavedChangesGuard] },
          { path: 'clinical-decision-support/services/:id', component: CdsServiceSetupComponent, canDeactivate: [unsavedChangesGuard] },
        ]),
        { provide: CdsApiService, useValue: api },
        { provide: ToastService, useValue: toast },
        { provide: UnsavedChangesPromptService, useValue: prompt },
        { provide: DialogService, useValue: dialog },
      ],
    });
    signInWith(TestBed.inject(AuthStore), permissions);
    const harness = await RouterTestingHarness.create();
    const page = await harness.navigateByUrl(url, CdsServiceSetupComponent);
    harness.detectChanges();
    const el = harness.routeNativeElement as HTMLElement;
    const q = <T extends Element>(sel: string) => el.querySelector(sel) as T;
    const type = (sel: string, value: string) => {
      const input = q<HTMLInputElement>(sel);
      input.value = value;
      input.dispatchEvent(new Event('input'));
      harness.detectChanges();
    };
    const save = () => {
      q<HTMLButtonElement>('[data-testid="cds-setup-save"]').click();
      harness.detectChanges();
    };
    return { harness, page, el, q, type, save, router: TestBed.inject(Router) };
  }

  describe('new service', () => {
    it('starts from the picked check: name, id, first hook, its settings form and prefetch', async () => {
      const { page, el, q } = await open('/clinical-decision-support/services/new?handler=care-gap');

      expect(page.isNew).toBeTrue();
      expect(q<HTMLSelectElement>('#cds-handler').value).toBe('care-gap');
      expect(q<HTMLSelectElement>('#cds-hook').value).toBe('patient-view');
      expect(q<HTMLInputElement>('#cds-title').value).toBe('Care gaps');
      expect(q<HTMLInputElement>('#cds-service-id').value).toBe('care-gaps');
      expect(q<HTMLInputElement>('#cds-setting-hba1cMonths').value).toBe('6');
      expect(el.querySelector('#cds-setting-egfrThreshold')).toBeNull();
      expect(el.textContent).toContain('Patient/{{context.patientId}}');
      expect(el.textContent).toContain('https://segue.example.org/cds-services/care-gaps');
      expect(el.textContent).toContain('sample data');
      expect(page.hasUnsavedChanges()).toBeFalse();
    });

    it('keeps the service id in step with the name until the id is edited', async () => {
      const { page, type, q } = await open('/clinical-decision-support/services/new?handler=renal-dose');

      type('#cds-title', 'Renal dose (ICU)');
      expect(q<HTMLInputElement>('#cds-service-id').value).toBe('renal-dose-icu');

      type('#cds-service-id', 'my-renal');
      type('#cds-title', 'Something else');
      expect(page.form.controls.serviceId.value).toBe('my-renal');
    });

    it('switching the check resets the settings and picks a hook it supports', async () => {
      const { page, harness, el } = await open('/clinical-decision-support/services/new?handler=renal-dose');
      expect(page.form.controls.hook.value).toBe('order-select');

      page.form.controls.handlerKey.setValue('care-gap');
      harness.detectChanges();

      expect(page.form.controls.hook.value).toBe('patient-view');
      expect(el.querySelector('#cds-setting-hba1cMonths')).not.toBeNull();
      expect(el.querySelector('#cds-setting-egfrThreshold')).toBeNull();
    });

    it('creates the service from the form and settings, then opens it without a leave prompt', async () => {
      const { save, type, router } = await open('/clinical-decision-support/services/new?handler=renal-dose');
      api.createService.and.returnValue(of(makeService({ id: 'new-1' })));
      api.getService.and.returnValue(of(makeService({ id: 'new-1' })));
      type('#cds-setting-egfrThreshold', '45');
      type('#cds-source-url', 'https://pharmacy.example.org');

      save();

      expect(api.createService).toHaveBeenCalledWith({
        serviceId: 'renal-dose-check', title: 'Renal dose check', handlerKey: 'renal-dose', hook: 'order-select',
        description: null, isEnabled: true, settings: { egfrThreshold: 45 }, cardSourceLabel: null,
        cardSourceUrl: 'https://pharmacy.example.org', allowedIssuerIds: [],
        fetchMissingData: true, fixes: null, overrideReasons: null, links: null,
      });
      expect(toast.success).toHaveBeenCalledWith('"Renal dose check" added.');
      await new Promise(resolve => setTimeout(resolve));
      expect(router.url).toBe('/clinical-decision-support/services/new-1');
    });

    it('blocks the save and points at the fields when the form is invalid', async () => {
      const { el, type, save } = await open('/clinical-decision-support/services/new?handler=renal-dose');
      type('#cds-service-id', 'Bad Id!');
      type('#cds-setting-egfrThreshold', '1');

      save();

      expect(api.createService).not.toHaveBeenCalled();
      expect(el.querySelector('[data-testid="cds-setup-error"]')?.textContent).toContain('Some fields need attention');
      expect(el.textContent).toContain('Use lower-case letters and numbers');
      expect(el.textContent).toContain('Must be 5 or more.');
    });

    it('shows the backend message (e.g. a duplicate service id) and stays on the page', async () => {
      const { el, save, page } = await open('/clinical-decision-support/services/new?handler=renal-dose');
      api.createService.and.returnValue(throwError(() => Object.assign(
        new HttpErrorResponse({ status: 400 }), { message: 'Service id "renal-dose-check" is already in use.' })));

      save();

      expect(el.querySelector('[data-testid="cds-setup-error"]')?.textContent).toContain('already in use');
      expect(page.saving()).toBeFalse();
    });
  });

  describe('existing service', () => {
    it('loads the service, fixes the check and fills the settings from the stored values', async () => {
      const { page, q, el } = await open('/clinical-decision-support/services/svc-1', CDS_ALL_PERMISSIONS,
        makeService({ settings: { egfrThreshold: 60 }, allowedIssuerIds: ['iss-1'], cardSourceUrl: 'https://x.org' }));

      expect(api.getService).toHaveBeenCalledWith('svc-1');
      expect(q<HTMLSelectElement>('#cds-handler').disabled).toBeTrue();
      expect(q<HTMLSelectElement>('#cds-hook').value).toBe('order-sign');
      expect(q<HTMLInputElement>('#cds-setting-egfrThreshold').value).toBe('60');
      expect(q<HTMLInputElement>('#cds-source-url').value).toBe('https://x.org');
      expect((el.querySelector('.issuer-list input') as HTMLInputElement).checked).toBeTrue();
      expect(el.textContent).toContain("The check can't be changed after the service is created.");
      expect(page.hasUnsavedChanges()).toBeFalse();
    });

    it('saves through the update endpoint and stays clean afterwards', async () => {
      const { page, type, save } = await open('/clinical-decision-support/services/svc-1');
      api.updateService.and.callFake((_id, req) => of(makeService({ title: req.title, settings: req.settings ?? {} })));
      type('#cds-title', 'Renal (ward 3)');
      expect(page.hasUnsavedChanges()).toBeTrue();

      save();

      expect(api.updateService).toHaveBeenCalledWith('svc-1', jasmine.objectContaining({ title: 'Renal (ward 3)', handlerKey: 'renal-dose' }));
      expect(toast.success).toHaveBeenCalledWith('Changes saved.');
      expect(page.hasUnsavedChanges()).toBeFalse();
    });

    it('is read-only for a role without edit', async () => {
      const { page, el, q } = await open('/clinical-decision-support/services/svc-1', ['cdshooks.view']);

      expect(page.canSave()).toBeFalse();
      expect(el.querySelector('[data-testid="cds-setup-save"]')).toBeNull();
      expect(el.textContent).toContain('You can view this service but not change it.');
      expect(q<HTMLInputElement>('#cds-title').disabled).toBeTrue();
      expect(q<HTMLInputElement>('#cds-setting-egfrThreshold').disabled).toBeTrue();
      expect((el.querySelector('.issuer-list input') as HTMLInputElement).disabled).toBeTrue();
      expect(el.querySelector('.cds-actions .btn-secondary')?.textContent).toContain('Back');
    });

    it('warns and blocks saving when the check is no longer available', async () => {
      const { el, q } = await open('/clinical-decision-support/services/svc-1', CDS_ALL_PERMISSIONS,
        makeService({ handlerKey: 'retired', handlerDisplayName: null }));

      expect(el.textContent).toContain('The check "retired" is no longer available');
      expect(q<HTMLButtonElement>('[data-testid="cds-setup-save"]').disabled).toBeTrue();
    });

    it('says so when the service no longer exists', async () => {
      const { el, page } = await open('/clinical-decision-support/services/gone', CDS_ALL_PERMISSIONS, 'not-found');

      expect(el.querySelector('.cds-notice--error')?.textContent).toContain('This service no longer exists.');
      expect(el.querySelector('form')).toBeNull();
      expect(page.hasUnsavedChanges()).toBeFalse();
    });
  });

  describe('Epic options', () => {
    const ALLERGY_SERVICE = makeService({
      id: 'svc-a', serviceId: 'drug-allergy', title: 'Drug allergy', handlerKey: 'drug-allergy', handlerDisplayName: 'Drug allergy',
      settings: {}, supportedFixTypes: ALLERGY.fixTypes,
    });

    beforeEach(() => (dialogAnswer = true));

    const click = (ctx: { q: <T extends Element>(sel: string) => T; harness: { detectChanges(): void } }, sel: string) => {
      ctx.q<HTMLElement>(sel).click();
      ctx.harness.detectChanges();
    };

    it('starts with fixes off, no badge, and saves no fixes without asking', async () => {
      const ctx = await open('/clinical-decision-support/services/new?handler=drug-allergy');
      api.createService.and.returnValue(of(makeService({ id: 'new-1' })));

      expect(ctx.q<HTMLInputElement>('[data-testid="cds-fixes-enabled"]').checked).toBeFalse();
      expect(ctx.el.querySelector('[data-testid="cds-fixes-badge"]')).toBeNull();
      expect(ctx.q<HTMLInputElement>('[data-testid="cds-fetch-missing"]').checked).toBeTrue();

      ctx.save();

      expect(dialog.open).not.toHaveBeenCalled();
      expect(api.createService).toHaveBeenCalledWith(jasmine.objectContaining({
        fetchMissingData: true, fixes: null, overrideReasons: null, links: null,
      }));
    });

    it('asks before the save that turns fixes on, and saves nothing when cancelled', async () => {
      dialogAnswer = false;
      const ctx = await open('/clinical-decision-support/services/svc-a', CDS_ALL_PERMISSIONS, ALLERGY_SERVICE);
      click(ctx, '[data-testid="cds-fixes-enabled"]');
      click(ctx, '#cds-fix-remove-draft');
      expect(ctx.el.querySelector('[data-testid="cds-fixes-badge"]')).not.toBeNull();

      ctx.save();

      expect(dialog.open).toHaveBeenCalledWith(ConfirmDialogComponent, jasmine.objectContaining({
        data: jasmine.objectContaining({ danger: true }),
      }));
      expect(api.updateService).not.toHaveBeenCalled();
      expect(ctx.page.instance()?.fixesEnabled).toBeFalse();
      expect(ctx.page.hasUnsavedChanges()).toBeTrue();
    });

    it('saves fixes switched on once confirmed', async () => {
      const ctx = await open('/clinical-decision-support/services/svc-a', CDS_ALL_PERMISSIONS, ALLERGY_SERVICE);
      api.updateService.and.callFake((_id, req) => of({
        ...ALLERGY_SERVICE, fixes: req.fixes!, fixesEnabled: req.fixes?.enabled === true,
      }));
      click(ctx, '[data-testid="cds-fixes-enabled"]');
      click(ctx, '#cds-fix-remove-draft');

      ctx.save();

      expect(dialog.open).toHaveBeenCalledTimes(1);
      expect(api.updateService).toHaveBeenCalledWith('svc-a', jasmine.objectContaining({
        fixes: {
          enabled: true,
          recommendedFixType: null,
          fixTypes: [
            { key: 'remove-draft', enabled: true, mappings: [] },
            { key: 'add-alternative', enabled: false, mappings: [] },
          ],
        },
      }));
      expect(ctx.page.instance()?.fixesEnabled).toBeTrue();
      expect(ctx.page.hasUnsavedChanges()).toBeFalse();
    });

    it('does not ask again when fixes were already on', async () => {
      const ctx = await open('/clinical-decision-support/services/svc-a', CDS_ALL_PERMISSIONS, {
        ...ALLERGY_SERVICE,
        fixesEnabled: true,
        fixes: { enabled: true, fixTypes: [{ key: 'remove-draft', enabled: true, mappings: [] }] },
      });
      api.updateService.and.returnValue(of(ALLERGY_SERVICE));
      expect(ctx.q<HTMLInputElement>('[data-testid="cds-fixes-enabled"]').checked).toBeTrue();
      expect(ctx.q<HTMLInputElement>('#cds-fix-remove-draft').checked).toBeTrue();

      ctx.type('#cds-title', 'Allergy (edited)');
      ctx.save();

      expect(dialog.open).not.toHaveBeenCalled();
      expect(api.updateService).toHaveBeenCalled();
    });

    it('blocks the save while a switched-on fix still needs a code, and accepts it once mapped', async () => {
      const ctx = await open('/clinical-decision-support/services/svc-a', CDS_ALL_PERMISSIONS, ALLERGY_SERVICE);
      api.updateService.and.returnValue(of(ALLERGY_SERVICE));
      click(ctx, '[data-testid="cds-fixes-enabled"]');
      click(ctx, '#cds-fix-add-alternative');

      ctx.save();
      expect(api.updateService).not.toHaveBeenCalled();
      expect(dialog.open).not.toHaveBeenCalled();
      expect(ctx.el.textContent).toContain("Add at least one code. This fix can't be offered without one.");

      click(ctx, '[data-testid="cds-fix-mapping-add"]');
      ctx.save();
      expect(api.updateService).withContext('code and name still blank').not.toHaveBeenCalled();
      expect(ctx.el.querySelectorAll('.cell--error').length).toBe(2);

      ctx.type('[aria-label="Code, row 1"]', '198013');
      ctx.type('[aria-label="Name, row 1"]', 'Naproxen');
      ctx.save();
      expect(api.updateService).toHaveBeenCalledWith('svc-a', jasmine.objectContaining({
        fixes: jasmine.objectContaining({
          fixTypes: [
            { key: 'remove-draft', enabled: false, mappings: [] },
            {
              key: 'add-alternative', enabled: true,
              mappings: [{ trigger: null, system: 'urn:com.epic.cdshooks.action.code.system.preference-list-item', code: '198013', display: 'Naproxen' }],
            },
          ],
        }),
      }));
    });

    it('saves "Fetch missing data" off, and counts it as an unsaved change', async () => {
      const ctx = await open('/clinical-decision-support/services/svc-1');
      api.updateService.and.returnValue(of(makeService({ fetchMissingData: false })));
      click(ctx, '[data-testid="cds-fetch-missing"]');
      expect(ctx.page.hasUnsavedChanges()).toBeTrue();

      ctx.save();

      expect(api.updateService).toHaveBeenCalledWith('svc-1', jasmine.objectContaining({ fetchMissingData: false }));
    });

    it('saves own dismiss reasons and card links, and counts them as unsaved changes', async () => {
      const ctx = await open('/clinical-decision-support/services/svc-1');
      api.updateService.and.returnValue(of(makeService()));
      click(ctx, '[data-testid="cds-reasons-customize"]');
      expect(ctx.page.hasUnsavedChanges()).toBeTrue();
      click(ctx, '[data-testid="cds-link-add"]');
      ctx.type('#cds-link-label-0', 'Dosing guide');
      ctx.type('#cds-link-url-0', 'http://guide.example.org');

      ctx.save();
      expect(api.updateService).withContext('http link').not.toHaveBeenCalled();

      ctx.type('#cds-link-url-0', 'https://guide.example.org');
      ctx.save();

      const request = api.updateService.calls.mostRecent().args[1];
      expect(request.overrideReasons?.length).toBe(4);
      expect(request.overrideReasons?.[0]).toEqual({ code: 'patient-tolerated', display: 'Patient has tolerated this before' });
      expect(request.links).toEqual([
        { label: 'Dosing guide', url: 'https://guide.example.org', type: 'absolute', appContext: null, autolaunchable: false },
      ]);
    });

    it('loads a service with its own reasons and links', async () => {
      const ctx = await open('/clinical-decision-support/services/svc-1', CDS_ALL_PERMISSIONS, makeService({
        overrideReasonsAreDefault: false, overrideReasons: [{ code: 'pt-refused', display: 'Patient refused' }],
        links: [{ label: 'App', url: 'https://app.example.org', type: 'smart', appContext: 'x', autolaunchable: true }],
        fetchMissingData: false,
      }));

      expect(ctx.q<HTMLInputElement>('#cds-reason-code-0').value).toBe('pt-refused');
      expect(ctx.q<HTMLSelectElement>('#cds-link-type-0').value).toBe('smart');
      expect(ctx.q<HTMLInputElement>('#cds-link-auto-0').checked).toBeTrue();
      expect(ctx.q<HTMLInputElement>('[data-testid="cds-fetch-missing"]').checked).toBeFalse();
      expect(ctx.page.hasUnsavedChanges()).toBeFalse();
    });

    it('links an existing service to its Epic setup sheet', async () => {
      const ctx = await open('/clinical-decision-support/services/svc-1');
      expect(ctx.q<HTMLAnchorElement>('[data-testid="cds-setup-sheet-link"]').getAttribute('href'))
        .toBe('/clinical-decision-support/connect?service=svc-1');
    });

    it('makes every Epic option read-only for a role without edit', async () => {
      const ctx = await open('/clinical-decision-support/services/svc-a', ['cdshooks.view'], ALLERGY_SERVICE);
      expect(ctx.q<HTMLInputElement>('[data-testid="cds-fetch-missing"]').disabled).toBeTrue();
      expect(ctx.q<HTMLInputElement>('[data-testid="cds-fixes-enabled"]').disabled).toBeTrue();
      expect(ctx.el.querySelector('[data-testid="cds-reasons-customize"]')).toBeNull();
      expect(ctx.el.querySelector('[data-testid="cds-link-add"]')).toBeNull();
    });
  });

  describe('unsaved changes', () => {
    it('asks before leaving with edited basics, and stays when the user cancels', async () => {
      const { type, router, page } = await open('/clinical-decision-support/services/svc-1');
      type('#cds-title', 'Edited');

      const left = await router.navigateByUrl('/clinical-decision-support/services');

      expect(prompt.confirmLeavePage).toHaveBeenCalledWith(page);
      expect(left).toBeFalse();
      expect(router.url).toBe('/clinical-decision-support/services/svc-1');
    });

    it('counts a settings edit and an allow-list tick as unsaved changes', async () => {
      const first = await open('/clinical-decision-support/services/svc-1');
      first.type('#cds-setting-egfrThreshold', '50');
      expect(first.page.hasUnsavedChanges()).toBeTrue();

      TestBed.resetTestingModule();
      const second = await open('/clinical-decision-support/services/svc-1');
      (second.el.querySelector('.issuer-list input') as HTMLInputElement).click();
      expect(second.page.hasUnsavedChanges()).toBeTrue();
    });

    it('leaves without asking when nothing changed', async () => {
      const { router } = await open('/clinical-decision-support/services/svc-1');
      expect(await router.navigateByUrl('/clinical-decision-support/services')).toBeTrue();
    });
  });
});
