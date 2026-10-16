import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { CdsCoverageRuleListComponent } from './cds-coverage-rule-list.component';
import { CdsCoverageRuleDialogComponent } from '../../dialogs/cds-coverage-rule-dialog/cds-coverage-rule-dialog.component';
import { CdsCoverageImportDialogComponent } from '../../dialogs/cds-coverage-import-dialog/cds-coverage-import-dialog.component';
import { AuthStore } from '../../../auth/store/auth.store';
import { DialogService } from '../../../core/services/dialog.service';
import { ToastService } from '../../../services/toast.service';
import { CdsCoverageApiService } from '../../services/cds-coverage-api.service';
import { CdsCoverageRule, CdsPayer } from '../../models/cds-coverage.models';
import {
  CDS_ALL_PERMISSIONS, makeCoverageApiSpy, makeCoverageRule, makeDialogSpy, makeImportResult, makePayer, makeToastSpy, signInWith,
} from '../../testing/cds-test-data';

describe('CdsCoverageRuleListComponent', () => {
  let api: ReturnType<typeof makeCoverageApiSpy>;
  let toast: ReturnType<typeof makeToastSpy>;
  let dialog: ReturnType<typeof makeDialogSpy>;

  function create(options: { rules?: CdsCoverageRule[]; payers?: CdsPayer[]; permissions?: string[]; dialogResult?: unknown } = {}) {
    api = makeCoverageApiSpy();
    const rules = options.rules ?? [makeCoverageRule()];
    api.getRules.and.returnValue(of({ items: rules, totalCount: rules.length, page: 1, pageSize: 25 }));
    toast = makeToastSpy();
    dialog = makeDialogSpy(options.dialogResult);
    TestBed.configureTestingModule({
      imports: [CdsCoverageRuleListComponent],
      providers: [
        provideNoopAnimations(),
        { provide: CdsCoverageApiService, useValue: api },
        { provide: ToastService, useValue: toast },
        { provide: DialogService, useValue: dialog },
      ],
    });
    signInWith(TestBed.inject(AuthStore), options.permissions ?? CDS_ALL_PERMISSIONS);
    const fixture = TestBed.createComponent(CdsCoverageRuleListComponent);
    fixture.componentRef.setInput('payers', options.payers ?? [makePayer()]);
    fixture.detectChanges();
    const changed = jasmine.createSpy('changed');
    fixture.componentInstance.changed.subscribe(changed);
    const el = fixture.nativeElement as HTMLElement;
    return { fixture, list: fixture.componentInstance, el, changed };
  }

  it('loads the first page and shows each rule in plain words', () => {
    const { el } = create({
      rules: [
        makeCoverageRule(),
        makeCoverageRule({
          id: 'r2', code: '705', codeMatch: 'prefix', covered: 'conditional', paNeeded: null, docNeeded: null, docPurposes: [],
          infoNeeded: ['performer'], effectiveFrom: '2026-01-01', effectiveTo: '2026-12-31', isEnabled: false, isEffectiveToday: false,
        }),
      ],
    });
    expect(api.getRules).toHaveBeenCalledWith({ payerId: undefined, code: undefined, search: undefined, enabled: undefined, page: 1, pageSize: 25 });
    const rows = el.querySelectorAll('[data-testid="cds-rule-row"]');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('Covered, prior authorization needed, clinical documentation');
    expect(rows[0].textContent).toContain('with the prior authorization request');
    expect(rows[0].textContent).toContain('Always');
    expect(rows[0].querySelector('[data-testid="cds-rule-today"]')).not.toBeNull();
    expect(rows[1].textContent).toContain('705*');
    expect(rows[1].textContent).toContain('Every code starting with 705');
    expect(rows[1].textContent).toContain('Needs: who performs it');
    expect(rows[1].textContent).toContain('2026-01-01 to 2026-12-31');
    expect(rows[1].querySelector('[data-testid="cds-rule-not-today"]')).not.toBeNull();
  });

  it('filters by payer and status at once, and by code and text after typing stops', fakeAsync(() => {
    const { el, list } = create();
    const payer = el.querySelector('[data-testid="cds-rule-filter-payer"]') as HTMLSelectElement;
    payer.value = 'payer-1';
    payer.dispatchEvent(new Event('change'));
    list.setEnabled('false');
    expect(api.getRules.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ payerId: 'payer-1', enabled: false, page: 1 }));

    api.getRules.calls.reset();
    list.setCode('705');
    list.setSearch(' mri ');
    expect(api.getRules).not.toHaveBeenCalled();
    tick(300);
    expect(api.getRules).toHaveBeenCalledTimes(1);
    expect(api.getRules.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ code: '705', search: 'mri' }));

    list.reset();
    expect(api.getRules.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ payerId: undefined, code: undefined, enabled: undefined }));
  }));

  it('pages', () => {
    const { list } = create();
    list.onPageChange({ pageIndex: 2, pageSize: 50 });
    expect(api.getRules.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ page: 3, pageSize: 50 }));
  });

  it('explains an empty list', () => {
    expect(create({ rules: [] }).el.querySelector('[data-testid="cds-rule-empty"]')?.textContent).toContain('Add a rule, or import a file');
    TestBed.resetTestingModule();
    expect(create({ rules: [], payers: [] }).el.querySelector('[data-testid="cds-rule-empty"]')?.textContent).toContain('Add a payer first');
  });

  it('opens the rule dialog with the payers and the filtered payer, then reloads', () => {
    const { list, changed } = create({ dialogResult: makeCoverageRule() });
    list.setPayer('payer-1');
    api.getRules.calls.reset();
    list.openAdd();
    expect(dialog.open).toHaveBeenCalledWith(CdsCoverageRuleDialogComponent, jasmine.objectContaining({
      data: { payers: [makePayer()], payerId: 'payer-1' },
    }));
    expect(toast.success).toHaveBeenCalledWith('Rule for 70551 added.');
    expect(api.getRules).toHaveBeenCalled();
    expect(changed).toHaveBeenCalled();
  });

  it('asks for a payer before a rule can be added', () => {
    const { list } = create({ payers: [] });
    list.openAdd();
    expect(dialog.open).not.toHaveBeenCalled();
    expect(toast.info).toHaveBeenCalled();
  });

  it('opens the import wizard and reports what it did', () => {
    const { list, changed } = create({ dialogResult: makeImportResult({ mode: 'apply', applied: true, added: 2, updated: 1, deleted: 1 }) });
    list.openImport();
    expect(dialog.open).toHaveBeenCalledWith(CdsCoverageImportDialogComponent, jasmine.objectContaining({ data: { payers: [makePayer()], payerId: null } }));
    expect(toast.success).toHaveBeenCalledWith('Rules imported: 2 added, 1 updated, 1 deleted.');
    expect(changed).toHaveBeenCalled();
  });

  it('exports the filtered payer in the chosen format', () => {
    const { list } = create();
    api.exportRules.and.returnValue(throwError(() => Object.assign(new HttpErrorResponse({ status: 500 }), { message: 'Export failed.' })));
    list.setPayer('payer-1');
    list.exportRules('json');
    expect(api.exportRules).toHaveBeenCalledWith('json', 'payer-1');
    expect(toast.error).toHaveBeenCalledWith('Export failed.');
  });

  it('deletes after confirmation', () => {
    const { list, changed } = create({ dialogResult: true });
    api.deleteRule.and.returnValue(of(undefined));
    list.confirmDelete(makeCoverageRule());
    expect(api.deleteRule).toHaveBeenCalledWith('rule-1');
    expect(changed).toHaveBeenCalled();
  });

  it('hides Add, Import and the row menu from a view-only role but keeps Export', () => {
    const { el } = create({ permissions: ['cdshooks.view'] });
    expect(el.querySelector('[data-testid="cds-rule-add"]')).toBeNull();
    expect(el.querySelector('[data-testid="cds-rule-import"]')).toBeNull();
    expect(el.querySelector('.btn-more')).toBeNull();
    expect(el.querySelector('[data-testid="cds-rule-export"]')).not.toBeNull();
  });

  it('drops the payer filter when that payer is deleted', () => {
    const { fixture, list } = create({ payers: [makePayer(), makePayer({ id: 'payer-2', name: 'Beta Plan' })] });
    list.setPayer('payer-2');
    api.getRules.calls.reset();

    fixture.componentRef.setInput('payers', [makePayer()]);
    fixture.detectChanges();

    expect(list.payerFilter()).toBe('');
    expect(api.getRules).toHaveBeenCalledWith(jasmine.objectContaining({ payerId: undefined, page: 1 }));
  });

  it('steps back to the last page that has rules after the last row of a page goes', () => {
    const { list } = create();
    list.onPageChange({ pageIndex: 1, pageSize: 25 });
    api.getRules.calls.reset();
    api.getRules.and.returnValues(
      of({ items: [], totalCount: 25, page: 2, pageSize: 25 }),
      of({ items: [makeCoverageRule()], totalCount: 25, page: 1, pageSize: 25 }),
    );

    list.load();

    expect(list.pageIndex()).toBe(0);
    expect(api.getRules.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ page: 1 }));
    expect(list.rules().length).toBe(1);
  });

  it('says so when the rules cannot be loaded', () => {
    const { list } = create();
    api.getRules.and.returnValue(throwError(() => new HttpErrorResponse({ status: 500 })));
    list.load();
    expect(toast.error).toHaveBeenCalledWith('Could not load the coverage rules.');
  });
});
