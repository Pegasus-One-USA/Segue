import { Component, OnInit, signal, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { forkJoin } from 'rxjs';

import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatDividerModule } from '@angular/material/divider';
import { MatTooltipModule } from '@angular/material/tooltip';

import { ToastService } from '../../../../../services/toast.service';
import { DestinationType } from '../../../../../destination-connections/models/destination-configuration.model';
import {
  TransformationRulesService, TransformationRule, TransformNodeType, TransformNodeSchema, NullPolicy, TransformErrorPolicy, TransformArrayMode,
} from '../transformation-rules.service';
import { getApplicableNodeTypes, ALL_NODE_TYPE_OPTIONS, nodeAbbr, nodeAccentVar } from '../transform-node-classifier';
import { RuleConfigFormComponent, applyNodeDefaults } from '../rule-config-form/rule-config-form.component';

export interface TransformRulesDialogData {
  resourceType: string;
  destinationType: DestinationType;
  /** The pipeline's upstream EHR vendor (e.g. "Epic") — already known from the wizard, applied to every
   *  step automatically; never asked for again here (see Gap 4 — no "only for this source" checkbox). */
  sourceSystem: string | null;
  /** The already-mapped destination columns for this resource, each carrying the SOURCE field it's fed
   *  from (fhirPath + coarse value type) — read automatically from the existing mapping, not re-asked. */
  columns: { tableName: string; targetName: string; sourceField?: string | null; sourceValueType?: string | null }[];
}

/** One saved-or-being-added step in a field's rule chain. Always Field-scoped and always carries the
 *  dialog's known sourceSystem/sourceField automatically — no scope or source picker in this screen
 *  (those live only in the Global Rules screen; see TransformationRuleListComponent). */
interface RuleStep {
  id: string | null;
  nodeType: TransformNodeType;
  config: Record<string, string>;
  order: number;
  isNew: boolean;
  saving: boolean;
  onNull: NullPolicy;
  onNullDefaultValue: string | null;
  errorPolicy: TransformErrorPolicy;
  arrayMode: TransformArrayMode;
  fhirWriteBackJsonPath: string | null;
}

interface ColumnRuleRow {
  tableName: string;
  targetName: string;
  sourceField?: string | null;
  applicableNodeTypes: { value: TransformNodeType; label: string }[];
  effectiveScope: string | null;
  effectiveNodeType: TransformNodeType | null;
  steps: RuleStep[];
  editing: boolean;
  previewSample: string;
  previewOutput: string | null;
  previewLoading: boolean;
  /** Read-only view of the rule(s) actually resolved for this field when there's no Field-level rule of its
   *  own yet (effectiveScope is broader than 'Field') — populated lazily the first time the scope chip is
   *  clicked. Null until fetched, distinct from an empty array (fetched, nothing found). */
  inheritedRules: TransformationRule[] | null;
  viewingInherited: boolean;
  loadingInherited: boolean;
}

@Component({
  selector: 'app-transform-rules-dialog',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatDialogModule, MatButtonModule, MatIconModule, MatProgressSpinnerModule,
    MatDividerModule, MatTooltipModule, RuleConfigFormComponent,
  ],
  templateUrl: './transform-rules-dialog.component.html',
  styleUrls: ['./transform-rules-dialog.component.scss'],
})
export class TransformRulesDialogComponent implements OnInit {
  private readonly dialogRef = inject(MatDialogRef<TransformRulesDialogComponent>);
  private readonly rulesService = inject(TransformationRulesService);
  private readonly toast = inject(ToastService);
  readonly data = inject<TransformRulesDialogData>(MAT_DIALOG_DATA);

  readonly loading = signal(true);
  readonly rows = signal<ColumnRuleRow[]>([]);
  private nodeSchemas: TransformNodeSchema[] = [];

  /** Maximizes to the same bounded "XL modal" size the Node Library Dialog itself uses (min(92vw,
   *  1100px) / min(88vh, 740px)) — NOT true edge-to-edge fullscreen. A per-column rule chain never needs
   *  the whole screen the way the mapping canvas does; capping it here keeps rounded corners/shadow
   *  intact and avoids dwarfing the wizard dialog sitting behind it. */
  readonly isMaximized = signal(false);
  toggleMaximize(): void {
    const next = !this.isMaximized();
    this.isMaximized.set(next);
    this.dialogRef.updateSize(next ? 'min(92vw, 1100px)' : '680px', next ? 'min(88vh, 740px)' : '');
  }

  ngOnInit(): void {
    this.loadRows();
  }

  schemaFor(nodeType: TransformNodeType): TransformNodeSchema | undefined {
    return this.nodeSchemas.find(s => s.nodeType === nodeType);
  }

  /** Purely cosmetic lookups for the step-chain visualization (badge glyph, rank-color accent, and the
   *  plain-language label for a chip's tooltip) — no state, no side effects, same underlying node data
   *  every other part of this dialog already reads. */
  protected readonly nodeAbbr = nodeAbbr;
  protected readonly nodeAccentVar = nodeAccentVar;
  nodeLabel(nodeType: TransformNodeType): string {
    return ALL_NODE_TYPE_OPTIONS.find(o => o.value === nodeType)?.label ?? nodeType;
  }

  private loadRows(): void {
    this.loading.set(true);
    forkJoin({
      schemas: this.rulesService.getNodeSchemas(),
      fieldRules: this.rulesService.list({ scope: 'Field', resourceType: this.data.resourceType }),
      previews: forkJoin(
        this.data.columns.map(c => this.rulesService.preview({
          destinationType: this.data.destinationType,
          resourceType: this.data.resourceType,
          destinationField: c.targetName,
          sampleValue: '',
          sourceSystem: this.data.sourceSystem,
          sourceField: c.sourceField,
        })),
      ),
    }).subscribe({
      next: ({ schemas, fieldRules, previews }) => {
        this.nodeSchemas = schemas;
        this.rows.set(this.data.columns.map((c, i) => {
          const preview = previews[i];
          const firstStep = preview.steps[0];
          const existingSteps = fieldRules
            .filter(r => r.destinationField === c.targetName)
            .sort((a, b) => a.order - b.order)
            .map((r): RuleStep => ({
              id: r.id,
              nodeType: r.nodeType,
              config: { ...(r.config ?? {}) },
              order: r.order,
              isNew: false,
              saving: false,
              onNull: r.onNull,
              onNullDefaultValue: r.onNullDefaultValue ?? null,
              errorPolicy: r.errorPolicy,
              arrayMode: r.arrayMode,
              fhirWriteBackJsonPath: r.fhirWriteBackJsonPath ?? null,
            }));

          return {
            tableName: c.tableName,
            targetName: c.targetName,
            sourceField: c.sourceField,
            applicableNodeTypes: getApplicableNodeTypes(c.sourceField, c.sourceValueType),
            effectiveScope: preview.effectiveScope,
            effectiveNodeType: firstStep?.nodeType ?? null,
            steps: existingSteps,
            editing: false,
            previewSample: '',
            previewOutput: null,
            previewLoading: false,
            inheritedRules: null,
            viewingInherited: false,
            loadingInherited: false,
          };
        }));
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.toast.error('Failed to load transformation rules for this resource.');
      },
    });
  }

  toggleEdit(row: ColumnRuleRow): void {
    row.editing = !row.editing;
    if (row.editing && row.steps.length === 0) {
      if (this.effectiveScopeIsInherited(row)) {
        this.overrideFromInherited(row);
        return;
      }
      this.addStep(row);
    }
    this.rows.set([...this.rows()]);
  }

  /** True when there's a rule resolved for this field that ISN'T already sitting in `row.steps` ready to
   *  edit inline — a broader tier (ResourceType/DestinationType/Global), or a Field-scoped rule matched by
   *  source field alone with no resource type of its own (so it doesn't show up in the per-resource-type
   *  `fieldRules` fetch `loadRows()` does). Either way there's something to view/clone rather than nothing. */
  effectiveScopeIsInherited(row: ColumnRuleRow): boolean {
    return !!row.effectiveScope && row.steps.length === 0;
  }

  /** Click handler for the scope chip — read-only, never creates or edits anything. */
  toggleInheritedView(row: ColumnRuleRow): void {
    if (!this.effectiveScopeIsInherited(row)) {
      return;
    }

    row.viewingInherited = !row.viewingInherited;
    if (row.viewingInherited && row.inheritedRules === null) {
      this.fetchInheritedRules(row, () => undefined);
    }
    this.rows.set([...this.rows()]);
  }

  /** Starts a Field-level override pre-filled from the currently-resolved rule(s), instead of blank schema
   *  defaults — fixes "Add rule" previously discarding what's actually running. */
  overrideFromInherited(row: ColumnRuleRow): void {
    if (row.inheritedRules !== null) {
      this.cloneIntoEditableSteps(row, row.inheritedRules);
      return;
    }

    this.fetchInheritedRules(row, rules => this.cloneIntoEditableSteps(row, rules));
  }

  private fetchInheritedRules(row: ColumnRuleRow, onLoaded: (rules: TransformationRule[]) => void): void {
    row.loadingInherited = true;
    this.rows.set([...this.rows()]);
    this.rulesService.getEffectiveRules({
      destinationType: this.data.destinationType,
      resourceType: this.data.resourceType,
      destinationField: row.targetName,
      sourceSystem: this.data.sourceSystem,
      sourceField: row.sourceField,
    }).subscribe({
      next: rules => {
        row.inheritedRules = rules;
        row.loadingInherited = false;
        this.rows.set([...this.rows()]);
        onLoaded(rules);
      },
      error: () => {
        row.loadingInherited = false;
        this.rows.set([...this.rows()]);
        this.toast.error('Failed to load the inherited rule.');
      },
    });
  }

  private cloneIntoEditableSteps(row: ColumnRuleRow, rules: TransformationRule[]): void {
    row.steps = rules.map((r, i): RuleStep => ({
      id: null,
      nodeType: r.nodeType,
      config: { ...(r.config ?? {}) },
      order: i,
      isNew: true,
      saving: false,
      onNull: r.onNull,
      onNullDefaultValue: r.onNullDefaultValue ?? null,
      errorPolicy: r.errorPolicy,
      arrayMode: r.arrayMode,
      fhirWriteBackJsonPath: r.fhirWriteBackJsonPath ?? null,
    }));
    row.editing = true;
    row.viewingInherited = false;
    this.rows.set([...this.rows()]);
  }

  addStep(row: ColumnRuleRow): void {
    const nodeType = row.applicableNodeTypes[0]?.value ?? ALL_NODE_TYPE_OPTIONS[0].value;
    row.steps.push({
      id: null,
      nodeType,
      config: applyNodeDefaults(this.schemaFor(nodeType), {}),
      order: row.steps.length,
      isNew: true,
      saving: false,
      onNull: 'Skip',
      onNullDefaultValue: null,
      errorPolicy: 'NullOut',
      arrayMode: 'Whole',
      fhirWriteBackJsonPath: null,
    });
    this.rows.set([...this.rows()]);
  }

  setOnNull(step: RuleStep, value: NullPolicy): void {
    step.onNull = value;
    this.rows.set([...this.rows()]);
  }

  setOnNullDefaultValue(step: RuleStep, value: string): void {
    step.onNullDefaultValue = value;
    this.rows.set([...this.rows()]);
  }

  setErrorPolicy(step: RuleStep, value: TransformErrorPolicy): void {
    step.errorPolicy = value;
    this.rows.set([...this.rows()]);
  }

  setArrayMode(step: RuleStep, value: TransformArrayMode): void {
    step.arrayMode = value;
    this.rows.set([...this.rows()]);
  }

  setFhirWriteBackJsonPath(step: RuleStep, value: string): void {
    step.fhirWriteBackJsonPath = value.trim() || null;
    this.rows.set([...this.rows()]);
  }

  onNodeTypeChange(step: RuleStep, nodeType: TransformNodeType): void {
    step.nodeType = nodeType;
    step.config = applyNodeDefaults(this.schemaFor(nodeType), {});
    this.rows.set([...this.rows()]);
  }

  removeStep(row: ColumnRuleRow, step: RuleStep): void {
    if (step.id) {
      step.saving = true;
      this.rows.set([...this.rows()]);
      this.rulesService.delete(step.id).subscribe({
        next: () => {
          row.steps = row.steps.filter(s => s !== step);
          this.renumber(row);
          this.toast.success(`Step removed for ${row.tableName}.${row.targetName}.`);
          this.loadRows();
        },
        error: () => {
          step.saving = false;
          this.rows.set([...this.rows()]);
          this.toast.error('Failed to delete the step.');
        },
      });
    } else {
      row.steps = row.steps.filter(s => s !== step);
      this.rows.set([...this.rows()]);
    }
  }

  moveStep(row: ColumnRuleRow, step: RuleStep, direction: -1 | 1): void {
    const index = row.steps.indexOf(step);
    const swapWith = index + direction;
    if (swapWith < 0 || swapWith >= row.steps.length) return;

    [row.steps[index], row.steps[swapWith]] = [row.steps[swapWith], row.steps[index]];
    this.renumber(row);
    this.rows.set([...this.rows()]);

    row.steps.filter(s => !s.isNew).forEach(s => this.saveStep(row, s, { silent: true }));
  }

  private renumber(row: ColumnRuleRow): void {
    row.steps.forEach((s, i) => { s.order = i; });
  }

  runPreview(row: ColumnRuleRow): void {
    row.previewLoading = true;
    this.rows.set([...this.rows()]);
    this.rulesService.preview({
      destinationType: this.data.destinationType,
      resourceType: this.data.resourceType,
      destinationField: row.targetName,
      sampleValue: row.previewSample,
      sourceSystem: this.data.sourceSystem,
      sourceField: row.sourceField,
    }).subscribe({
      next: result => {
        row.previewOutput = result.finalValue === null || result.finalValue === undefined
          ? '(null)'
          : typeof result.finalValue === 'object' ? JSON.stringify(result.finalValue) : String(result.finalValue);
        row.previewLoading = false;
        this.rows.set([...this.rows()]);
      },
      error: () => {
        row.previewLoading = false;
        this.rows.set([...this.rows()]);
        this.toast.error('Preview failed.');
      },
    });
  }

  saveStep(row: ColumnRuleRow, step: RuleStep, opts: { silent?: boolean } = {}): void {
    step.saving = true;
    this.rows.set([...this.rows()]);
    this.rulesService.save({
      id: step.id,
      scope: 'Field',
      nodeType: step.nodeType,
      config: step.config,
      resourceType: this.data.resourceType,
      destinationField: row.targetName,
      sourceSystem: this.data.sourceSystem,
      sourceField: row.sourceField,
      order: step.order,
      onNull: step.onNull,
      onNullDefaultValue: step.onNullDefaultValue,
      errorPolicy: step.errorPolicy,
      arrayMode: step.arrayMode,
      fhirWriteBackJsonPath: step.fhirWriteBackJsonPath,
    }).subscribe({
      next: saved => {
        step.id = saved.id;
        step.isNew = false;
        step.saving = false;
        this.rows.set([...this.rows()]);
        if (!opts.silent) {
          this.toast.success(`Rule saved for ${row.tableName}.${row.targetName}.`);
          this.loadRows();
        }
      },
      error: () => {
        step.saving = false;
        this.rows.set([...this.rows()]);
        if (!opts.silent) this.toast.error('Failed to save the rule.');
      },
    });
  }

  close(): void {
    this.dialogRef.close();
  }
}
