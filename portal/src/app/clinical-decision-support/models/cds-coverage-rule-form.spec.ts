import { FormControl } from '@angular/forms';
import { buildCoverageRuleForm, questionnaireCanonicalValidator, readCoverageRuleForm } from './cds-coverage-rule-form';
import { makeCoverageRule } from '../testing/cds-test-data';

describe('cds-coverage-rule-form', () => {
  it('accepts a blank or http(s) questionnaire, with or without |version', () => {
    const check = (value: string) => questionnaireCanonicalValidator(new FormControl(value));
    expect(check('')).toBeNull();
    expect(check('http://payer.example.org/Questionnaire/q|1.0')).toBeNull();
    expect(check('https://payer.example.org/Questionnaire/q')).toBeNull();
    expect(check('ftp://payer.example.org/q')).toEqual({ canonical: true });
    expect(check('not a url')).toEqual({ canonical: true });
  });

  it('flags each CRD rule the API enforces', () => {
    const form = buildCoverageRuleForm(null, 'payer-1');
    form.patchValue({ code: '1', covered: 'not-covered', paNeeded: 'auth-needed' });
    expect(form.hasError('notCoveredWithPa')).toBeTrue();
    form.patchValue({ covered: 'covered', docPurposes: ['withpa'], questionnaire: 'http://q.example/Q' });
    expect(form.hasError('purposesNeedDoc')).toBeTrue();
    expect(form.hasError('questionnaireNeedsDoc')).toBeTrue();
    form.patchValue({ docNeeded: 'clinical', infoNeeded: ['location'] });
    expect(form.hasError('infoNeedsConditional')).toBeTrue();
    form.patchValue({ docNeeded: 'conditional', effectiveFrom: '2026-02-01', effectiveTo: '2026-01-31' });
    expect(form.hasError('infoNeedsConditional')).toBeFalse();
    expect(form.hasError('effectiveOrder')).toBeTrue();
    form.patchValue({ effectiveTo: '2026-02-01' });
    expect(form.valid).toBeTrue();
  });

  it('round-trips a rule, sending "always" and blanks as null', () => {
    const form = buildCoverageRuleForm(makeCoverageRule({ reason: null, policyUrl: null }), null);
    expect(form.controls.effectiveFrom.value).toBe('');
    expect(readCoverageRuleForm(form)).toEqual({
      payerId: 'payer-1', system: 'http://www.ama-assn.org/go/cpt', code: '70551', codeMatch: 'exact', covered: 'covered',
      paNeeded: 'auth-needed', docNeeded: 'clinical', docPurposes: ['withpa'], infoNeeded: [], reason: null, policyUrl: null,
      questionnaire: null, effectiveFrom: null, effectiveTo: null, isEnabled: true,
    });
  });
});
