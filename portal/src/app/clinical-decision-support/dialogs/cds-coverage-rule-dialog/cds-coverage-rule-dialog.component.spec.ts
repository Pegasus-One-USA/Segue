import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { CdsCoverageRuleDialogComponent, CdsCoverageRuleDialogData } from './cds-coverage-rule-dialog.component';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';
import { AuthStore } from '../../../auth/store/auth.store';
import { ToastService } from '../../../services/toast.service';
import { CdsCoverageApiService } from '../../services/cds-coverage-api.service';
import {
  CDS_ALL_PERMISSIONS, makeCoverageApiSpy, makeCoverageRule, makePayer, makeToastSpy, signInWith,
} from '../../testing/cds-test-data';

describe('CdsCoverageRuleDialogComponent', () => {
  let api: ReturnType<typeof makeCoverageApiSpy>;
  let ref: { close: jasmine.Spy; attemptClose: jasmine.Spy };
  let toast: ReturnType<typeof makeToastSpy>;

  function create(data: CdsCoverageRuleDialogData, permissions = CDS_ALL_PERMISSIONS) {
    api = makeCoverageApiSpy();
    toast = makeToastSpy();
    ref = { close: jasmine.createSpy('close'), attemptClose: jasmine.createSpy('attemptClose') };
    TestBed.configureTestingModule({
      imports: [CdsCoverageRuleDialogComponent],
      providers: [
        { provide: CdsCoverageApiService, useValue: api },
        { provide: ToastService, useValue: toast },
        { provide: DialogRef, useValue: ref },
        { provide: DIALOG_DATA, useValue: data },
      ],
    });
    signInWith(TestBed.inject(AuthStore), permissions);
    const fixture = TestBed.createComponent(CdsCoverageRuleDialogComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const submit = () => {
      (el.querySelector('[data-testid="cds-rule-dialog-save"]') as HTMLButtonElement).click();
      fixture.detectChanges();
    };
    return { fixture, dialog: fixture.componentInstance, el, submit };
  }

  const payers = [makePayer(), makePayer({ id: 'payer-2', name: 'Beta Health' })];

  it('needs a payer and a code, and picks the only payer by itself', () => {
    const { el, submit } = create({ payers });
    submit();
    expect(api.createRule).not.toHaveBeenCalled();
    expect(el.textContent).toContain('Pick the payer this rule is for.');
    expect(el.textContent).toContain('Enter the order code.');

    TestBed.resetTestingModule();
    const single = create({ payers: [makePayer()] });
    expect(single.dialog.form.controls.payerId.value).toBe('payer-1');
  });

  it('creates a rule with CPT, exact match and blank dates sent as null', () => {
    const { dialog, submit } = create({ payers, payerId: 'payer-2' });
    const saved = makeCoverageRule({ payerId: 'payer-2' });
    api.createRule.and.returnValue(of(saved));
    dialog.form.patchValue({ code: ' 70553 ', paNeeded: 'auth-needed', reason: 'Needs prior authorization.' });

    submit();

    expect(api.createRule).toHaveBeenCalledOnceWith({
      payerId: 'payer-2', system: 'http://www.ama-assn.org/go/cpt', code: '70553', codeMatch: 'exact', covered: 'covered',
      paNeeded: 'auth-needed', docNeeded: null, docPurposes: [], infoNeeded: [], reason: 'Needs prior authorization.',
      policyUrl: null, questionnaire: null, effectiveFrom: null, effectiveTo: null, isEnabled: true,
    });
    expect(ref.close).toHaveBeenCalledWith(saved);
  });

  it('switches off and clears prior authorization for a not-covered rule', () => {
    const { dialog, el, fixture } = create({ payers, payerId: 'payer-1' });
    dialog.form.controls.paNeeded.setValue('auth-needed');
    dialog.form.controls.covered.setValue('not-covered');
    fixture.detectChanges();
    expect(dialog.form.controls.paNeeded.disabled).toBeTrue();
    expect(dialog.form.controls.paNeeded.value).toBe('');
    expect(el.querySelector('[data-testid="cds-rule-pa-off"]')).not.toBeNull();
    dialog.form.controls.covered.setValue('covered');
    expect(dialog.form.controls.paNeeded.enabled).toBeTrue();
  });

  it('offers documentation purposes and the questionnaire only with documentation needed', () => {
    const { dialog, el, fixture } = create({ payers, payerId: 'payer-1' });
    expect(dialog.form.controls.docPurposes.disabled).toBeTrue();
    expect(dialog.form.controls.questionnaire.disabled).toBeTrue();
    expect(el.querySelector('[data-testid="cds-rule-purpose-off"]')).not.toBeNull();

    dialog.form.controls.docNeeded.setValue('clinical');
    dialog.form.controls.docPurposes.setValue(['withpa']);
    dialog.form.controls.questionnaire.setValue('http://payer.example.org/Questionnaire/q|1');
    fixture.detectChanges();
    expect(dialog.form.controls.docPurposes.enabled).toBeTrue();
    expect(el.querySelector('[data-testid="cds-rule-purpose-off"]')).toBeNull();

    dialog.form.controls.docNeeded.setValue('');
    expect(dialog.form.controls.docPurposes.value).toEqual([]);
    expect(dialog.form.controls.questionnaire.value).toBe('');
    expect(dialog.form.valid).toBeFalse(); // the code is still empty
    dialog.form.controls.code.setValue('70553');
    expect(dialog.form.valid).toBeTrue();
  });

  it('offers information needed only when something depends on details', () => {
    const { dialog } = create({ payers, payerId: 'payer-1' });
    expect(dialog.form.controls.infoNeeded.disabled).toBeTrue();
    dialog.form.controls.paNeeded.setValue('conditional');
    expect(dialog.form.controls.infoNeeded.enabled).toBeTrue();
    dialog.form.controls.infoNeeded.setValue(['performer', 'location']);
    dialog.form.controls.paNeeded.setValue('auth-needed');
    expect(dialog.form.controls.infoNeeded.disabled).toBeTrue();
    expect(dialog.form.controls.infoNeeded.value).toEqual([]);
  });

  it('checks the policy link is https, the questionnaire is a canonical and the end is not before the start', () => {
    const { dialog, el, submit } = create({ payers, payerId: 'payer-1' });
    dialog.form.patchValue({ code: '70553', policyUrl: 'http://payer.example.org/policy', docNeeded: 'clinical' });
    dialog.form.controls.questionnaire.setValue('not a url');
    dialog.form.patchValue({ effectiveFrom: '2026-05-01', effectiveTo: '2026-04-30' });
    submit();
    expect(api.createRule).not.toHaveBeenCalled();
    expect(el.textContent).toContain('starting with https://');
    expect(el.textContent).toContain("questionnaire's address");
    expect(el.querySelector('[data-testid="cds-rule-group-errors"]')?.textContent).toContain('"Ends on" must be on or after "Starts on".');
  });

  it('shows whether the rule is in use today', () => {
    const { dialog, el, fixture } = create({ payers, payerId: 'payer-1' });
    const today = () => el.querySelector('[data-testid="cds-rule-effective-today"]')?.textContent?.trim();
    expect(today()).toContain('In use today.');
    dialog.form.controls.effectiveFrom.setValue('2999-01-01');
    fixture.detectChanges();
    expect(today()).toContain('Not in use yet: starts on 2999-01-01.');
    dialog.form.patchValue({ effectiveFrom: '', effectiveTo: '2001-01-01' });
    fixture.detectChanges();
    expect(today()).toContain('No longer in use');
    dialog.form.patchValue({ effectiveTo: '', isEnabled: false });
    fixture.detectChanges();
    expect(today()).toContain('the rule is off');
  });

  it('keeps the payer fixed when editing and sends every field', () => {
    const rule = makeCoverageRule({ effectiveFrom: '2026-01-01', effectiveTo: '2026-12-31', codeMatch: 'prefix', code: '705' });
    const { dialog, el, submit } = create({ payers, rule });
    expect(dialog.isEdit).toBeTrue();
    expect(dialog.form.controls.payerId.disabled).toBeTrue();
    expect((el.querySelector('[data-testid="cds-rule-payer-fixed"]') as HTMLInputElement).value).toBe('Acme Health Plan');
    api.updateRule.and.returnValue(of(rule));

    submit();

    expect(api.updateRule).toHaveBeenCalledOnceWith('rule-1', jasmine.objectContaining({
      payerId: 'payer-1', code: '705', codeMatch: 'prefix', paNeeded: 'auth-needed', docNeeded: 'clinical',
      docPurposes: ['withpa'], policyUrl: 'https://payer.example.org/policy/mri', effectiveFrom: '2026-01-01', effectiveTo: '2026-12-31',
    }));
  });

  it('shows "always" as a blank start date', () => {
    const { dialog } = create({ payers, rule: makeCoverageRule() });
    expect(dialog.form.controls.effectiveFrom.value).toBe('');
  });

  it('shows the API reason when the save is refused', () => {
    const { dialog, el, submit } = create({ payers, payerId: 'payer-1' });
    api.createRule.and.returnValue(throwError(() => Object.assign(
      new HttpErrorResponse({ status: 400 }), { message: 'This payer already has a rule for that code.' })));
    dialog.form.patchValue({ code: '70553' });
    submit();
    expect(el.querySelector('[data-testid="cds-rule-dialog-error"]')?.textContent).toContain('already has a rule');
    expect(ref.close).not.toHaveBeenCalled();
  });

  it('will not save without the permission', () => {
    const { dialog, submit } = create({ payers, rule: makeCoverageRule() }, ['cdshooks.view']);
    dialog.form.patchValue({ reason: 'Other' });
    submit();
    expect(api.updateRule).not.toHaveBeenCalled();
    expect(toast.warning).toHaveBeenCalled();
  });

  it('cancel goes through the unsaved-changes check', () => {
    const { el } = create({ payers });
    (el.querySelector('.btn-secondary') as HTMLButtonElement).click();
    expect(ref.attemptClose).toHaveBeenCalled();
  });
});
