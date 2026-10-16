import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { CDS_IMPORT_MAX_BYTES, CdsCoverageImportDialogComponent } from './cds-coverage-import-dialog.component';
import { DIALOG_DATA, DialogRef, DialogService } from '../../../core/services/dialog.service';
import { ConfirmDialogComponent } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { AuthStore } from '../../../auth/store/auth.store';
import { ToastService } from '../../../services/toast.service';
import { CdsCoverageApiService } from '../../services/cds-coverage-api.service';
import {
  CDS_ALL_PERMISSIONS, makeCoverageApiSpy, makeDialogSpy, makeImportResult, makePayer, makeToastSpy, signInWith,
} from '../../testing/cds-test-data';

describe('CdsCoverageImportDialogComponent', () => {
  let api: ReturnType<typeof makeCoverageApiSpy>;
  let dialog: ReturnType<typeof makeDialogSpy>;
  let toast: ReturnType<typeof makeToastSpy>;
  let ref: { close: jasmine.Spy; attemptClose: jasmine.Spy };

  function create(options: { payerId?: string | null; confirm?: unknown; permissions?: string[] } = {}) {
    api = makeCoverageApiSpy();
    dialog = makeDialogSpy(options.confirm ?? true);
    toast = makeToastSpy();
    ref = { close: jasmine.createSpy('close'), attemptClose: jasmine.createSpy('attemptClose') };
    TestBed.configureTestingModule({
      imports: [CdsCoverageImportDialogComponent],
      providers: [
        { provide: CdsCoverageApiService, useValue: api },
        { provide: DialogService, useValue: dialog },
        { provide: ToastService, useValue: toast },
        { provide: DialogRef, useValue: ref },
        { provide: DIALOG_DATA, useValue: { payers: [makePayer()], payerId: options.payerId ?? null } },
      ],
    });
    signInWith(TestBed.inject(AuthStore), options.permissions ?? CDS_ALL_PERMISSIONS);
    const fixture = TestBed.createComponent(CdsCoverageImportDialogComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
    const choose = (file: File) => {
      fixture.componentInstance.onFileChange({ target: { files: [file] } } as unknown as Event);
      fixture.detectChanges();
    };
    return { fixture, cmp: fixture.componentInstance, el, q, choose };
  }

  const csv = () => new File(['payer,system,code,covered\n'], 'rules.csv', { type: 'text/csv' });

  it('needs a file before it can check one', () => {
    const { q } = create();
    expect((q('cds-import-preview-btn') as HTMLButtonElement).disabled).toBeTrue();
  });

  it('takes the format from the file name and refuses a file over 5 MB', () => {
    const { cmp, choose, q, fixture } = create();
    choose(new File(['{}'], 'rules.json'));
    expect(cmp.options.controls.format.value).toBe('json');
    expect(q('cds-import-file-name')?.textContent).toContain('rules.json');

    const big = new File(['x'], 'big.csv');
    Object.defineProperty(big, 'size', { value: CDS_IMPORT_MAX_BYTES + 1 });
    choose(big);
    fixture.detectChanges();
    expect(cmp.fileError()).toContain('larger than 5 MB');
    expect((q('cds-import-preview-btn') as HTMLButtonElement).disabled).toBeTrue();
  });

  it('checks the file with the chosen payer and strategy, writing nothing', () => {
    const { cmp, choose, q, fixture } = create({ payerId: 'payer-1' });
    api.importRules.and.returnValue(of(makeImportResult()));
    const file = csv();
    choose(file);
    q('cds-import-preview-btn')!.click();
    fixture.detectChanges();
    expect(api.importRules).toHaveBeenCalledOnceWith({ file, format: 'csv', mode: 'preview', strategy: 'merge', payerId: 'payer-1' });
    expect(cmp.step()).toBe('preview');
    expect(q('cds-import-preview')).not.toBeNull();
    expect((q('cds-import-apply') as HTMLButtonElement).disabled).toBeFalse();
  });

  it('keeps Import off while any row has an error', () => {
    const { choose, q, fixture } = create();
    api.importRules.and.returnValue(of(makeImportResult({ errorCount: 1 })));
    choose(csv());
    q('cds-import-preview-btn')!.click();
    fixture.detectChanges();
    expect((q('cds-import-apply') as HTMLButtonElement).disabled).toBeTrue();
    expect(q('cds-import-blocked')).not.toBeNull();
  });

  it('keeps Import off when the file changes nothing', () => {
    const { choose, q, fixture } = create();
    api.importRules.and.returnValue(of(makeImportResult({ added: 0, updated: 0, unchanged: 2 })));
    choose(csv());
    q('cds-import-preview-btn')!.click();
    fixture.detectChanges();
    expect((q('cds-import-apply') as HTMLButtonElement).disabled).toBeTrue();
    expect(q('cds-import-nothing')).not.toBeNull();
  });

  it('applies a merge and closes with the result', () => {
    const { choose, q, fixture } = create();
    const applied = makeImportResult({ mode: 'apply', applied: true });
    api.importRules.and.returnValues(of(makeImportResult()), of(applied));
    choose(csv());
    q('cds-import-preview-btn')!.click();
    fixture.detectChanges();
    q('cds-import-apply')!.click();
    expect(api.importRules.calls.mostRecent().args[0].mode).toBe('apply');
    expect(dialog.open).not.toHaveBeenCalled();
    expect(ref.close).toHaveBeenCalledWith(applied);
  });

  it('warns about replace and confirms before deleting rules', () => {
    const { cmp, choose, q, fixture } = create({ confirm: false });
    cmp.options.controls.strategy.setValue('replace');
    fixture.detectChanges();
    expect(q('cds-import-replace-warning')?.textContent).toContain('deletes');
    api.importRules.and.returnValue(of(makeImportResult({ strategy: 'replace', deleted: 3 })));
    choose(csv());
    q('cds-import-preview-btn')!.click();
    fixture.detectChanges();
    q('cds-import-apply')!.click();
    expect(dialog.open).toHaveBeenCalledWith(ConfirmDialogComponent, jasmine.objectContaining({
      data: jasmine.objectContaining({ title: 'Delete 3 rules?', danger: true }),
    }));
    expect(api.importRules).toHaveBeenCalledTimes(1);
    expect(ref.close).not.toHaveBeenCalled();
  });

  it('shows the new problems when the apply finds errors after all', () => {
    const { cmp, choose, q, fixture } = create();
    api.importRules.and.returnValues(of(makeImportResult()), of(makeImportResult({ mode: 'apply', applied: false, errorCount: 1 })));
    choose(csv());
    q('cds-import-preview-btn')!.click();
    fixture.detectChanges();
    q('cds-import-apply')!.click();
    fixture.detectChanges();
    expect(ref.close).not.toHaveBeenCalled();
    expect(cmp.preview()?.errorCount).toBe(1);
    expect(q('cds-import-error')?.textContent).toContain('Nothing was saved');
  });

  it('goes back to step 1 when a choice changes after the preview', () => {
    const { cmp, choose, q, fixture } = create();
    api.importRules.and.returnValue(of(makeImportResult()));
    choose(csv());
    q('cds-import-preview-btn')!.click();
    cmp.options.controls.payerId.setValue('payer-1');
    fixture.detectChanges();
    expect(cmp.step()).toBe('choose');
    expect(cmp.preview()).toBeNull();
  });

  it('shows the API message when the file cannot be checked', () => {
    const { choose, q, fixture } = create();
    api.importRules.and.returnValue(throwError(() => Object.assign(new HttpErrorResponse({ status: 400 }), { message: 'format must be csv or json.' })));
    choose(csv());
    q('cds-import-preview-btn')!.click();
    fixture.detectChanges();
    expect(q('cds-import-error')?.textContent).toContain('format must be csv or json.');
  });

  it('downloads a template', () => {
    const { q } = create();
    api.getImportTemplate.and.returnValue(throwError(() => new HttpErrorResponse({ status: 500 })));
    q('cds-import-template-json')!.click();
    expect(api.getImportTemplate).toHaveBeenCalledWith('json');
    expect(toast.error).toHaveBeenCalled();
  });

  it('will not check a file without the edit permission', () => {
    const { choose, q } = create({ permissions: ['cdshooks.view'] });
    choose(csv());
    q('cds-import-preview-btn')!.click();
    expect(api.importRules).not.toHaveBeenCalled();
    expect(toast.warning).toHaveBeenCalled();
  });

  it('offers Replace only with the delete permission', () => {
    const { q } = create({ permissions: ['cdshooks.view', 'cdshooks.edit'] });
    expect(q('cds-import-replace')).toBeNull();
    expect(q('cds-import-replace-unavailable')?.textContent).toContain('permission to delete');
  });

  it('asks before closing once a file is chosen', () => {
    const { cmp, choose } = create();
    expect(cmp.hasUnsavedChanges()).toBeFalse();
    choose(csv());
    expect(cmp.hasUnsavedChanges()).toBeTrue();
    cmp.cancel();
    expect(ref.attemptClose).toHaveBeenCalled();
  });
});
