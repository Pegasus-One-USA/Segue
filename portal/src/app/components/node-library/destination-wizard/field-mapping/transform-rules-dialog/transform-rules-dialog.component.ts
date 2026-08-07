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
import { MatSelectModule } from '@angular/material/select';
import { MatInputModule } from '@angular/material/input';
import { MatFormFieldModule } from '@angular/material/form-field';

import { ToastService } from '../../../../../services/toast.service';
import { DestinationType } from '../../../../../destination-connections/models/destination-configuration.model';
import {
  TransformationRulesService, TransformNodeType, TransformNodeSchema,
} from '../transformation-rules.service';
import { getApplicableNodeTypes, ALL_NODE_TYPE_OPTIONS } from '../transform-node-classifier';
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
}

@Component({
  selector: 'app-transform-rules-dialog',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatDialogModule, MatButtonModule, MatIconModule, MatProgressSpinnerModule,
    MatDividerModule, MatTooltipModule, MatSelectModule, MatInputModule, MatFormFieldModule, RuleConfigFormComponent,
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

  ngOnInit(): void {
    this.loadRows();
  }

  schemaFor(nodeType: TransformNodeType): TransformNodeSchema | undefined {
    return this.nodeSchemas.find(s => s.nodeType === nodeType);
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
      this.addStep(row);
    }
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
    });
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
