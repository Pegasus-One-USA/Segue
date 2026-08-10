import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { forkJoin } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectModule } from '@angular/material/select';
import { MatInputModule } from '@angular/material/input';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { ToastService } from '../../../services/toast.service';
import { DestinationType } from '../../../destination-connections/models/destination-configuration.model';
import { MappingCatalogService, FhirElement } from '../../../services/mapping-catalog.service';
import {
  TransformationRulesService, TransformationRule, TransformNodeType, TransformScope, TransformNodeSchema,
  NullPolicy, TransformErrorPolicy, TransformArrayMode,
} from '../../../components/node-library/destination-wizard/field-mapping/transformation-rules.service';
import {
  ALL_NODE_TYPE_OPTIONS, getApplicableNodeTypes,
} from '../../../components/node-library/destination-wizard/field-mapping/transform-node-classifier';
import {
  RuleConfigFormComponent, applyNodeDefaults,
} from '../../../components/node-library/destination-wizard/field-mapping/rule-config-form/rule-config-form.component';

const DESTINATION_TYPE_OPTIONS: DestinationType[] = [
  'SqlServer', 'AzureSql', 'PostgreSql', 'MySql', 'Mongo', 'Csv', 'Sftp', 'FhirRepository',
];

// Global/ResourceType/DestinationType have no natural home inside any one mapping wizard, so they're always
// created here. 'Field' is a special case: the wizard's Rules button (TransformRulesDialogComponent) creates
// the common kind — tied to one resource type + one already-mapped column — but a Field rule with NO
// resource type (matched purely by source field name, e.g. "wherever birthDate shows up") has no wizard
// context to be created from either, so it's managed here too. Workflow (tied to one pipeline run) still has
// no UI anywhere.
const BROAD_SCOPE_OPTIONS: { value: TransformScope; label: string }[] = [
  { value: 'Global', label: 'Global — every destination, every resource' },
  { value: 'ResourceType', label: 'Resource type — any destination' },
  { value: 'DestinationType', label: 'Destination type — any resource' },
  { value: 'Field', label: 'Field — matched by source field, any resource type' },
];

interface RuleStep {
  id: string | null;
  nodeType: TransformNodeType;
  config: Record<string, string>;
  order: number;
  saving: boolean;
  onNull: NullPolicy;
  onNullDefaultValue: string | null;
  errorPolicy: TransformErrorPolicy;
  arrayMode: TransformArrayMode;
  fhirWriteBackJsonPath: string | null;
}

/** One target (scope + whatever keys that scope uses) and its ordered chain of steps. Grouped from the
 *  flat rule list the backend returns, keyed by everything except node/config/order (see groupKey). */
interface RuleTargetGroup {
  key: string;
  scope: TransformScope;
  resourceType: string | null;
  destinationType: DestinationType | null;
  destinationField: string | null;
  sourceField: string | null;
  steps: RuleStep[];
  editing: boolean;
}

function groupKey(r: { scope: TransformScope; resourceType?: string | null; destinationType?: DestinationType | null; destinationField?: string | null; sourceField?: string | null }): string {
  return [r.scope, r.resourceType ?? '', r.destinationType ?? '', r.destinationField ?? '', r.sourceField ?? ''].join('|');
}

interface NewTargetForm {
  scope: TransformScope;
  resourceType: string;
  destinationType: DestinationType | '';
  destinationField: string;
  sourceField: string;
}

function emptyTargetForm(): NewTargetForm {
  return { scope: 'Global', resourceType: '', destinationType: '', destinationField: '', sourceField: '' };
}

/**
 * Manages Global/ResourceType/DestinationType-scoped transformation rules — the "set once, applies
 * everywhere" tiers with no natural home inside any one mapping wizard, since (unlike a Field-level
 * override) they aren't tied to an already-mapped column. Each target can carry a multi-step chain, same
 * as the per-field Rules dialog.
 */
@Component({
  selector: 'app-transformation-rule-list',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatButtonModule, MatIconModule, MatSelectModule, MatInputModule,
    MatFormFieldModule, MatTooltipModule, MatProgressSpinnerModule, RuleConfigFormComponent,
  ],
  templateUrl: './transformation-rule-list.component.html',
  styleUrls: ['./transformation-rule-list.component.scss'],
})
export class TransformationRuleListComponent implements OnInit {
  private readonly rulesService = inject(TransformationRulesService);
  private readonly catalogService = inject(MappingCatalogService);
  private readonly toast = inject(ToastService);

  readonly scopeOptions = BROAD_SCOPE_OPTIONS;
  readonly destinationTypeOptions = DESTINATION_TYPE_OPTIONS;
  readonly resourceTypeOptions = ['Patient', 'Observation', 'Encounter', 'Condition', 'Practitioner'];

  readonly loading = signal(true);
  // Defense-in-depth: the nav tab that links here is already hidden while the feature flag reads hidden
  // (Settings > System Settings > General, "TransformationRules:Hidden"), but a direct URL nav bypasses
  // that — this catches it and shows a disabled message instead of the editor.
  readonly disabled = signal(false);
  readonly groups = signal<RuleTargetGroup[]>([]);
  readonly scopeFilter = signal<TransformScope | ''>('');
  readonly creatingNew = signal(false);
  readonly newTarget = signal<NewTargetForm>(emptyTargetForm());
  readonly sourceFieldOptions = signal<FhirElement[]>([]);

  private nodeSchemas: TransformNodeSchema[] = [];

  ngOnInit(): void {
    this.rulesService.isHidden().subscribe(hidden => {
      this.disabled.set(hidden);
      if (hidden) {
        this.loading.set(false);
      } else {
        this.load();
      }
    });
  }

  schemaFor(nodeType: TransformNodeType): TransformNodeSchema | undefined {
    return this.nodeSchemas.find(s => s.nodeType === nodeType);
  }

  applicableNodeTypesFor(group: { sourceField: string | null }) {
    return group.sourceField ? getApplicableNodeTypes(group.sourceField, null) : ALL_NODE_TYPE_OPTIONS;
  }

  load(): void {
    this.loading.set(true);
    forkJoin({
      schemas: this.rulesService.getNodeSchemas(),
      rules: forkJoin(BROAD_SCOPE_OPTIONS.map(o =>
        new Promise<TransformationRule[]>((resolve, reject) =>
          this.rulesService.list({ scope: o.value }).subscribe({ next: resolve, error: reject })))),
    }).subscribe({
      next: ({ schemas, rules }) => {
        this.nodeSchemas = schemas;
        // A Field-scoped rule with a resource type set belongs to the wizard's per-column Rules button
        // (TransformRulesDialogComponent) — this screen only owns the resource-agnostic kind (no resource
        // type), so the wizard's own rules never show up here to be edited out of their context.
        const flat = rules.flat().filter(r => r.scope !== 'Field' || !r.resourceType);
        const byKey = new Map<string, RuleTargetGroup>();
        for (const r of flat) {
          const key = groupKey(r);
          let group = byKey.get(key);
          if (!group) {
            group = {
              key, scope: r.scope, resourceType: r.resourceType ?? null, destinationType: r.destinationType ?? null,
              destinationField: r.destinationField ?? null, sourceField: r.sourceField ?? null, steps: [], editing: false,
            };
            byKey.set(key, group);
          }
          group.steps.push({
            id: r.id, nodeType: r.nodeType, config: { ...(r.config ?? {}) }, order: r.order, saving: false,
            onNull: r.onNull, onNullDefaultValue: r.onNullDefaultValue ?? null, errorPolicy: r.errorPolicy, arrayMode: r.arrayMode,
            fhirWriteBackJsonPath: r.fhirWriteBackJsonPath ?? null,
          });
        }
        byKey.forEach(g => g.steps.sort((a, b) => a.order - b.order));
        this.groups.set(Array.from(byKey.values()).sort((a, b) => a.scope.localeCompare(b.scope)));
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.toast.error('Failed to load transformation rules.');
      },
    });
  }

  get filteredGroups(): RuleTargetGroup[] {
    const filter = this.scopeFilter();
    return filter ? this.groups().filter(g => g.scope === filter) : this.groups();
  }

  // ── Source-field dropdown — populated from the real FHIR catalog once a resource type is chosen ──
  onResourceTypeChangeForNewTarget(resourceType: string): void {
    this.newTarget.set({ ...this.newTarget(), resourceType, sourceField: '' });
    if (resourceType) {
      this.catalogService.fields(resourceType).subscribe(fields => this.sourceFieldOptions.set(fields));
    } else {
      this.sourceFieldOptions.set([]);
    }
  }

  setNewTargetScope(scope: TransformScope): void { this.newTarget.set({ ...this.newTarget(), scope }); }
  setNewTargetDestinationType(dt: DestinationType | ''): void { this.newTarget.set({ ...this.newTarget(), destinationType: dt }); }
  setNewTargetDestinationField(field: string): void { this.newTarget.set({ ...this.newTarget(), destinationField: field }); }
  setNewTargetSourceField(sourceField: string): void { this.newTarget.set({ ...this.newTarget(), sourceField }); }

  startNewTarget(): void {
    this.creatingNew.set(true);
    this.newTarget.set(emptyTargetForm());
    this.sourceFieldOptions.set([]);
  }

  cancelNewTarget(): void {
    this.creatingNew.set(false);
  }

  createTarget(): void {
    const t = this.newTarget();
    if (t.scope === 'ResourceType' && !t.resourceType.trim()) {
      this.toast.error('Resource type is required for a resource-type-scoped rule.');
      return;
    }
    if (t.scope === 'DestinationType' && !t.destinationType) {
      this.toast.error('Destination type is required for a destination-type-scoped rule.');
      return;
    }
    if (t.scope === 'Field' && !t.sourceField) {
      this.toast.error('Source field is required for a field-scoped rule — that’s what it matches on instead of a resource type.');
      return;
    }

    const applicable = t.sourceField ? getApplicableNodeTypes(t.sourceField, null) : ALL_NODE_TYPE_OPTIONS;
    const nodeType = applicable[0]?.value ?? ALL_NODE_TYPE_OPTIONS[0].value;
    const group: RuleTargetGroup = {
      key: groupKey({ scope: t.scope, resourceType: t.resourceType, destinationType: t.destinationType || null, destinationField: t.destinationField, sourceField: t.sourceField }),
      scope: t.scope,
      resourceType: t.scope === 'ResourceType' ? t.resourceType.trim() : null,
      destinationType: t.scope === 'DestinationType' ? (t.destinationType || null) : null,
      destinationField: t.destinationField.trim() || null,
      sourceField: t.sourceField || null,
      steps: [{
        id: null, nodeType, config: applyNodeDefaults(this.schemaFor(nodeType), {}), order: 0, saving: false,
        onNull: 'Skip', onNullDefaultValue: null, errorPolicy: 'NullOut', arrayMode: 'Whole',
        fhirWriteBackJsonPath: null,
      }],
      editing: true,
    };
    this.groups.set([group, ...this.groups()]);
    this.creatingNew.set(false);
  }

  toggleEdit(group: RuleTargetGroup): void {
    group.editing = !group.editing;
    this.groups.set([...this.groups()]);
  }

  addStep(group: RuleTargetGroup): void {
    const applicable = this.applicableNodeTypesFor(group);
    const nodeType = applicable[0]?.value ?? ALL_NODE_TYPE_OPTIONS[0].value;
    group.steps.push({
      id: null, nodeType, config: applyNodeDefaults(this.schemaFor(nodeType), {}), order: group.steps.length, saving: false,
      onNull: 'Skip', onNullDefaultValue: null, errorPolicy: 'NullOut', arrayMode: 'Whole',
      fhirWriteBackJsonPath: null,
    });
    this.groups.set([...this.groups()]);
  }

  onNodeTypeChange(step: RuleStep, nodeType: TransformNodeType): void {
    step.nodeType = nodeType;
    step.config = applyNodeDefaults(this.schemaFor(nodeType), {});
    this.groups.set([...this.groups()]);
  }

  setOnNull(step: RuleStep, value: NullPolicy): void {
    step.onNull = value;
    this.groups.set([...this.groups()]);
  }

  setOnNullDefaultValue(step: RuleStep, value: string): void {
    step.onNullDefaultValue = value;
    this.groups.set([...this.groups()]);
  }

  setErrorPolicy(step: RuleStep, value: TransformErrorPolicy): void {
    step.errorPolicy = value;
    this.groups.set([...this.groups()]);
  }

  setArrayMode(step: RuleStep, value: TransformArrayMode): void {
    step.arrayMode = value;
    this.groups.set([...this.groups()]);
  }

  setFhirWriteBackJsonPath(step: RuleStep, value: string): void {
    step.fhirWriteBackJsonPath = value.trim() || null;
    this.groups.set([...this.groups()]);
  }

  moveStep(group: RuleTargetGroup, step: RuleStep, direction: -1 | 1): void {
    const index = group.steps.indexOf(step);
    const swapWith = index + direction;
    if (swapWith < 0 || swapWith >= group.steps.length) return;
    [group.steps[index], group.steps[swapWith]] = [group.steps[swapWith], group.steps[index]];
    group.steps.forEach((s, i) => { s.order = i; });
    this.groups.set([...this.groups()]);
    group.steps.filter(s => s.id).forEach(s => this.saveStep(group, s, { silent: true }));
  }

  saveStep(group: RuleTargetGroup, step: RuleStep, opts: { silent?: boolean } = {}): void {
    step.saving = true;
    this.groups.set([...this.groups()]);
    this.rulesService.save({
      id: step.id,
      scope: group.scope,
      nodeType: step.nodeType,
      config: step.config,
      resourceType: group.resourceType,
      destinationType: group.destinationType,
      destinationField: group.destinationField,
      sourceField: group.sourceField,
      order: step.order,
      onNull: step.onNull,
      onNullDefaultValue: step.onNullDefaultValue,
      errorPolicy: step.errorPolicy,
      arrayMode: step.arrayMode,
      fhirWriteBackJsonPath: step.fhirWriteBackJsonPath,
    }).subscribe({
      next: saved => {
        step.id = saved.id;
        step.saving = false;
        this.groups.set([...this.groups()]);
        if (!opts.silent) this.toast.success('Rule saved.');
      },
      error: () => {
        step.saving = false;
        this.groups.set([...this.groups()]);
        if (!opts.silent) this.toast.error('Failed to save the rule.');
      },
    });
  }

  removeStep(group: RuleTargetGroup, step: RuleStep): void {
    const remove = () => {
      group.steps = group.steps.filter(s => s !== step);
      if (group.steps.length === 0) {
        this.groups.set(this.groups().filter(g => g !== group));
      } else {
        this.groups.set([...this.groups()]);
      }
    };

    if (step.id) {
      this.rulesService.delete(step.id).subscribe({
        next: () => { this.toast.success('Rule removed.'); remove(); },
        error: () => this.toast.error('Failed to delete the rule.'),
      });
    } else {
      remove();
    }
  }
}
