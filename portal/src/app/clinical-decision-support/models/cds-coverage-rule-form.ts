import { AbstractControl, FormControl, FormGroup, ValidationErrors, Validators } from '@angular/forms';
import {
  CDS_COVERAGE_LIMITS,
  CdsCodeMatch,
  CdsCoverageRule,
  CdsCovered,
  CdsDocNeeded,
  CdsDocPurpose,
  CdsInfoNeeded,
  CdsPaNeeded,
  SaveCdsCoverageRuleRequest,
} from './cds-coverage.models';
import { absoluteHttpsUrlValidator } from './cds-validators';

// The coverage rule editor's form, with the same dependent-field rules as CdsCoverageRule.Validate on the API, so
// the admin sees each problem before saving. The API still checks everything.

export type CdsCoverageRuleForm = FormGroup<{
  payerId: FormControl<string>;
  system: FormControl<string>;
  code: FormControl<string>;
  codeMatch: FormControl<CdsCodeMatch>;
  covered: FormControl<CdsCovered>;
  paNeeded: FormControl<CdsPaNeeded | ''>;
  docNeeded: FormControl<CdsDocNeeded | ''>;
  docPurposes: FormControl<CdsDocPurpose[]>;
  infoNeeded: FormControl<CdsInfoNeeded[]>;
  reason: FormControl<string>;
  policyUrl: FormControl<string>;
  questionnaire: FormControl<string>;
  effectiveFrom: FormControl<string>;
  effectiveTo: FormControl<string>;
  isEnabled: FormControl<boolean>;
}>;

/** Group error keys (each matches one CRD rule the API enforces). */
export type CdsCoverageRuleGroupError =
  | 'notCoveredWithPa'
  | 'purposesNeedDoc'
  | 'questionnaireNeedsDoc'
  | 'infoNeedsConditional'
  | 'effectiveOrder';

/** Blank passes; otherwise http(s)://..., optionally followed by |version. Error key: `canonical`. */
export function questionnaireCanonicalValidator(control: AbstractControl): ValidationErrors | null {
  const value = typeof control.value === 'string' ? control.value.trim() : '';
  if (!value) return null;
  try {
    const url = new URL(value.split('|')[0]);
    return url.protocol === 'https:' || url.protocol === 'http:' ? null : { canonical: true };
  } catch {
    return { canonical: true };
  }
}

/** The CRD invariants that span several fields (crd-ci-q1, q2, q3) and the date order. */
export function coverageRuleGroupValidator(group: AbstractControl): ValidationErrors | null {
  const v = (group as CdsCoverageRuleForm).getRawValue();
  const errors: Partial<Record<CdsCoverageRuleGroupError, true>> = {};
  if (v.covered === 'not-covered' && v.paNeeded) errors.notCoveredWithPa = true;
  if (!v.docNeeded && v.docPurposes.length > 0) errors.purposesNeedDoc = true;
  if (!v.docNeeded && v.questionnaire.trim()) errors.questionnaireNeedsDoc = true;
  const anyConditional = v.covered === 'conditional' || v.paNeeded === 'conditional' || v.docNeeded === 'conditional';
  if (!anyConditional && v.infoNeeded.length > 0) errors.infoNeedsConditional = true;
  if (v.effectiveTo && v.effectiveTo < (v.effectiveFrom || '2000-01-01')) errors.effectiveOrder = true;
  return Object.keys(errors).length ? errors : null;
}

export function buildCoverageRuleForm(rule: CdsCoverageRule | null, payerId: string | null): CdsCoverageRuleForm {
  return new FormGroup(
    {
      payerId: new FormControl(rule?.payerId ?? payerId ?? '', { nonNullable: true, validators: [Validators.required] }),
      system: new FormControl(rule?.system ?? 'http://www.ama-assn.org/go/cpt', {
        nonNullable: true, validators: [Validators.required, Validators.maxLength(CDS_COVERAGE_LIMITS.systemMax)],
      }),
      code: new FormControl(rule?.code ?? '', {
        nonNullable: true, validators: [Validators.required, Validators.maxLength(CDS_COVERAGE_LIMITS.codeMax)],
      }),
      codeMatch: new FormControl<CdsCodeMatch>(rule?.codeMatch ?? 'exact', { nonNullable: true }),
      covered: new FormControl<CdsCovered>(rule?.covered ?? 'covered', { nonNullable: true, validators: [Validators.required] }),
      paNeeded: new FormControl<CdsPaNeeded | ''>(rule?.paNeeded ?? '', { nonNullable: true }),
      docNeeded: new FormControl<CdsDocNeeded | ''>(rule?.docNeeded ?? '', { nonNullable: true }),
      docPurposes: new FormControl<CdsDocPurpose[]>([...(rule?.docPurposes ?? [])], { nonNullable: true }),
      infoNeeded: new FormControl<CdsInfoNeeded[]>([...(rule?.infoNeeded ?? [])], { nonNullable: true }),
      reason: new FormControl(rule?.reason ?? '', { nonNullable: true, validators: [Validators.maxLength(CDS_COVERAGE_LIMITS.reasonMax)] }),
      policyUrl: new FormControl(rule?.policyUrl ?? '', {
        nonNullable: true, validators: [Validators.maxLength(CDS_COVERAGE_LIMITS.urlMax), absoluteHttpsUrlValidator],
      }),
      questionnaire: new FormControl(rule?.questionnaire ?? '', {
        nonNullable: true, validators: [Validators.maxLength(CDS_COVERAGE_LIMITS.urlMax), questionnaireCanonicalValidator],
      }),
      // 2000-01-01 is the API's "always"; shown as blank.
      effectiveFrom: new FormControl(rule && rule.effectiveFrom !== '2000-01-01' ? rule.effectiveFrom : '', { nonNullable: true }),
      effectiveTo: new FormControl(rule?.effectiveTo ?? '', { nonNullable: true }),
      isEnabled: new FormControl(rule?.isEnabled ?? true, { nonNullable: true }),
    },
    { validators: [coverageRuleGroupValidator] },
  );
}

export function readCoverageRuleForm(form: CdsCoverageRuleForm): SaveCdsCoverageRuleRequest {
  const v = form.getRawValue();
  const blank = (value: string) => value.trim() || null;
  return {
    payerId: v.payerId,
    system: v.system.trim(),
    code: v.code.trim(),
    codeMatch: v.codeMatch,
    covered: v.covered,
    paNeeded: v.paNeeded || null,
    docNeeded: v.docNeeded || null,
    docPurposes: v.docNeeded ? v.docPurposes : [],
    infoNeeded: v.infoNeeded,
    reason: blank(v.reason),
    policyUrl: blank(v.policyUrl),
    questionnaire: v.docNeeded ? blank(v.questionnaire) : null,
    effectiveFrom: v.effectiveFrom || null,
    effectiveTo: v.effectiveTo || null,
    isEnabled: v.isEnabled,
  };
}
