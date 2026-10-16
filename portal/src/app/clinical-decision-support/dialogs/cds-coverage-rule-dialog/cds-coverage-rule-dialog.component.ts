import { Component, computed, inject, signal } from '@angular/core';
import { ReactiveFormsModule } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { HttpErrorResponse } from '@angular/common/http';
import { MatIconModule } from '@angular/material/icon';
import { map, startWith } from 'rxjs';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { CdsCoverageApiService } from '../../services/cds-coverage-api.service';
import {
  CDS_COVERAGE_LIMITS,
  CDS_COVERED_VALUES,
  CDS_DOC_NEEDED_VALUES,
  CDS_DOC_PURPOSE_VALUES,
  CDS_INFO_NEEDED_VALUES,
  CDS_PA_NEEDED_FOR_RULES,
  CdsCoverageRule,
  CdsPayer,
} from '../../models/cds-coverage.models';
import {
  CdsCoverageRuleGroupError,
  buildCoverageRuleForm,
  readCoverageRuleForm,
} from '../../models/cds-coverage-rule-form';
import {
  cdsCoveredLabel,
  cdsDocNeededLabel,
  cdsDocPurposeLabel,
  cdsInfoNeededLabel,
  cdsPaNeededLabel,
} from '../../models/cds-coverage-labels';
import { CdsCodeCheckboxesComponent, CdsCodeOption } from '../../components/cds-code-checkboxes/cds-code-checkboxes.component';

export interface CdsCoverageRuleDialogData {
  /** The payers to pick from (a new rule needs one). */
  payers: CdsPayer[];
  /** Set when editing. */
  rule?: CdsCoverageRule;
  /** Pre-selects this payer for a new rule. */
  payerId?: string | null;
}

/** Code systems an order code usually comes from (the field accepts any system). */
export const CDS_ORDER_CODE_SYSTEMS: CdsCodeOption[] = [
  { value: 'http://www.ama-assn.org/go/cpt', label: 'CPT' },
  { value: 'https://bluebutton.cms.gov/resources/codesystem/hcpcs', label: 'HCPCS' },
  { value: 'http://loinc.org', label: 'LOINC' },
  { value: 'http://snomed.info/sct', label: 'SNOMED CT' },
  { value: 'http://www.nlm.nih.gov/research/umls/rxnorm', label: 'RxNorm' },
];

const GROUP_ERRORS: Record<CdsCoverageRuleGroupError, string> = {
  notCoveredWithPa: 'A rule that says "Not covered" cannot also state prior authorization.',
  purposesNeedDoc: 'Pick which documentation is needed before choosing what it is for.',
  questionnaireNeedsDoc: 'A questionnaire needs "Documentation needed" to be set.',
  infoNeedsConditional: '"Information needed" is only allowed when coverage, prior authorization or documentation depends on details.',
  effectiveOrder: '"Ends on" must be on or after "Starts on".',
};

/** Today in UTC as yyyy-MM-dd (the API decides "in use today" in UTC). */
export function cdsUtcToday(): string {
  return new Date().toISOString().slice(0, 10);
}

/**
 * Add or edit one coverage rule: for orders with this code, what the payer requires. Mirrors the API's rules, so a
 * field that does not apply is switched off (and cleared) rather than refused on save: documentation purposes and
 * the questionnaire need "documentation needed"; "information needed" needs a "depends on details" value; a
 * not-covered rule has no prior authorization. The payer of a saved rule cannot change.
 */
@Component({
  selector: 'app-cds-coverage-rule-dialog',
  standalone: true,
  imports: [ReactiveFormsModule, MatIconModule, CdsCodeCheckboxesComponent],
  templateUrl: './cds-coverage-rule-dialog.component.html',
  styleUrl: './cds-coverage-rule-dialog.component.scss',
})
// DialogRef.attemptClose() reads `form.dirty` itself, so leaving with edits asks first.
export class CdsCoverageRuleDialogComponent {
  private readonly api = inject(CdsCoverageApiService);
  private readonly actionGuard = inject(PermissionActionGuard);
  readonly dialogRef = inject<DialogRef<CdsCoverageRule>>(DialogRef);
  readonly data = inject(DIALOG_DATA) as CdsCoverageRuleDialogData;

  readonly rule = this.data.rule ?? null;
  readonly isEdit = !!this.rule;
  readonly limits = CDS_COVERAGE_LIMITS;
  readonly systems = CDS_ORDER_CODE_SYSTEMS;
  readonly payers = this.data.payers;

  readonly coveredOptions = CDS_COVERED_VALUES.map(v => ({ value: v, label: cdsCoveredLabel(v) }));
  readonly paOptions = CDS_PA_NEEDED_FOR_RULES.map(v => ({ value: v, label: cdsPaNeededLabel(v) }));
  readonly docOptions = CDS_DOC_NEEDED_VALUES.map(v => ({ value: v, label: cdsDocNeededLabel(v) }));
  readonly purposeOptions: CdsCodeOption[] = CDS_DOC_PURPOSE_VALUES.map(v => ({ value: v, label: cdsDocPurposeLabel(v) }));
  readonly infoOptions: CdsCodeOption[] = CDS_INFO_NEEDED_VALUES.map(v => ({ value: v, label: cdsInfoNeededLabel(v) }));

  readonly submitted = signal(false);
  readonly saving = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly form = buildCoverageRuleForm(this.rule, this.data.payerId ?? (this.payers.length === 1 ? this.payers[0].id : null));

  /** The whole form (disabled fields too), re-read on every change so the template can follow it. */
  private readonly raw = toSignal(
    this.form.valueChanges.pipe(startWith(null), map(() => this.form.getRawValue())),
    { initialValue: this.form.getRawValue() },
  );

  readonly docSet = computed(() => !!this.raw().docNeeded);
  readonly anyConditional = computed(() => {
    const v = this.raw();
    return v.covered === 'conditional' || v.paNeeded === 'conditional' || v.docNeeded === 'conditional';
  });
  readonly notCovered = computed(() => this.raw().covered === 'not-covered');

  /** Whether the rule, as filled in now, would be used today (on, and today within its dates). */
  readonly effectiveToday = computed(() => {
    const v = this.raw();
    if (!v.isEnabled) return { on: false, text: 'Not in use: the rule is off.' };
    const today = cdsUtcToday();
    const from = v.effectiveFrom || '2000-01-01';
    if (today < from) return { on: false, text: `Not in use yet: starts on ${from}.` };
    if (v.effectiveTo && today > v.effectiveTo) return { on: false, text: `No longer in use: ended on ${v.effectiveTo}.` };
    return { on: true, text: 'In use today.' };
  });

  readonly groupErrors = computed(() => {
    this.raw();
    const errors = this.form.errors ?? {};
    return (Object.keys(GROUP_ERRORS) as CdsCoverageRuleGroupError[]).filter(k => errors[k]).map(k => GROUP_ERRORS[k]);
  });

  constructor() {
    if (this.isEdit) this.form.controls.payerId.disable({ emitEvent: false });
    this.applyDependencies();
    // Each of these can switch other fields on or off.
    this.form.controls.covered.valueChanges.subscribe(() => this.applyDependencies());
    this.form.controls.paNeeded.valueChanges.subscribe(() => this.applyDependencies());
    this.form.controls.docNeeded.valueChanges.subscribe(() => this.applyDependencies());
  }

  /** Switches off (and clears) the fields the API would refuse for the current choices. */
  applyDependencies(): void {
    const c = this.form.controls;
    const quiet = { emitEvent: false };

    if (c.covered.value === 'not-covered') {
      if (c.paNeeded.value) c.paNeeded.setValue('', quiet);
      c.paNeeded.disable(quiet);
    } else {
      c.paNeeded.enable(quiet);
    }

    if (!c.docNeeded.value) {
      if (c.docPurposes.value.length) c.docPurposes.setValue([], quiet);
      if (c.questionnaire.value) c.questionnaire.setValue('', quiet);
      c.docPurposes.disable(quiet);
      c.questionnaire.disable(quiet);
    } else {
      c.docPurposes.enable(quiet);
      c.questionnaire.enable(quiet);
    }

    const anyConditional = c.covered.value === 'conditional' || c.paNeeded.value === 'conditional' || c.docNeeded.value === 'conditional';
    if (!anyConditional) {
      if (c.infoNeeded.value.length) c.infoNeeded.setValue([], quiet);
      c.infoNeeded.disable(quiet);
    } else {
      c.infoNeeded.enable(quiet);
    }

    // The changes above were quiet; one refresh tells the checkbox groups and the template.
    c.docPurposes.updateValueAndValidity();
    c.infoNeeded.updateValueAndValidity();
    this.form.updateValueAndValidity();
  }

  hasError(name: 'payerId' | 'system' | 'code' | 'reason' | 'policyUrl' | 'questionnaire', error?: string): boolean {
    const control = this.form.controls[name];
    const visible = control.touched || this.submitted();
    return visible && (error ? control.hasError(error) : control.invalid);
  }

  payerName(id: string): string {
    return this.payers.find(p => p.id === id)?.name ?? this.rule?.payerName ?? 'Unknown payer';
  }

  isSaveInProgress(): boolean {
    return this.saving();
  }

  cancel(): void {
    this.dialogRef.attemptClose();
  }

  save(): void {
    const code = this.isEdit ? 'cdshooks.edit' : 'cdshooks.create';
    if (!this.actionGuard.ensure(code, `You do not have permission to ${this.isEdit ? 'edit' : 'add'} coverage rules.`)) return;

    this.submitted.set(true);
    this.errorMessage.set(null);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const request = readCoverageRuleForm(this.form);
    this.saving.set(true);
    const request$ = this.rule ? this.api.updateRule(this.rule.id, request) : this.api.createRule(request);
    request$.subscribe({
      next: saved => {
        this.saving.set(false);
        this.dialogRef.close(saved);
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.errorMessage.set(err.message || 'Could not save the rule.');
      },
    });
  }
}
