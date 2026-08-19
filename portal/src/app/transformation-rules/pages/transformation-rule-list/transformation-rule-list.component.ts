import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { forkJoin } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { ToastService } from '../../../services/toast.service';
import { DestinationType, DeIdentificationProfileDto } from '../../../destination-connections/models/destination-configuration.model';
import { DeIdentificationProfileService } from '../../../destination-connections/services/deidentification-profile.service';
import { MappingCatalogService, FhirElement } from '../../../services/mapping-catalog.service';
import {
  TransformationRulesService, TransformationRule, TransformNodeType, TransformScope, TransformNodeSchema,
  NullPolicy, TransformErrorPolicy, TransformArrayMode, TransformExecutionPhase,
} from '../../../components/node-library/destination-wizard/field-mapping/transformation-rules.service';
import {
  ALL_NODE_TYPE_OPTIONS, getApplicableNodeTypes,
} from '../../../components/node-library/destination-wizard/field-mapping/transform-node-classifier';
import {
  RuleConfigFormComponent, applyNodeDefaults,
} from '../../../components/node-library/destination-wizard/field-mapping/rule-config-form/rule-config-form.component';

// Pre-mapping HashingMasking rules walk raw source JSON by path (see SafeHarborDeIdentificationService) and
// so support strategies a post-mapping scalar transform can't (removing a property outright, generalizing a
// date/ZIP) alongside the existing hash/mask/redact vocabulary.
const PRE_MAPPING_HASHING_MASKING_MODES = ['hash', 'mask', 'redact', 'remove', 'generalizeDateToYear', 'generalizeZip3'];

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
  executionPhase: TransformExecutionPhase;
  deIdentificationProfileId: string | null;
  steps: RuleStep[];
  editing: boolean;
  previewOpen?: boolean;
  previewResourceType?: string;
  previewSampleJson?: string;
  previewResultJson?: string | null;
  previewRunning?: boolean;
}

function groupKey(r: {
  scope: TransformScope; resourceType?: string | null; destinationType?: DestinationType | null;
  destinationField?: string | null; sourceField?: string | null; executionPhase?: TransformExecutionPhase;
  deIdentificationProfileId?: string | null;
}): string {
  return [
    r.scope, r.resourceType ?? '', r.destinationType ?? '', r.destinationField ?? '', r.sourceField ?? '',
    r.executionPhase ?? 'PostMapping', r.deIdentificationProfileId ?? '',
  ].join('|');
}

interface NewTargetForm {
  scope: TransformScope;
  resourceType: string;
  destinationType: DestinationType | '';
  destinationField: string;
  sourceField: string;
  executionPhase: TransformExecutionPhase;
  deIdentificationProfileId: string;
}

function emptyTargetForm(): NewTargetForm {
  return {
    scope: 'Global', resourceType: '', destinationType: '', destinationField: '', sourceField: '',
    executionPhase: 'PostMapping', deIdentificationProfileId: '',
  };
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
    CommonModule, FormsModule, MatButtonModule, MatIconModule, MatTooltipModule, MatProgressSpinnerModule,
    RuleConfigFormComponent,
  ],
  templateUrl: './transformation-rule-list.component.html',
  styleUrls: ['./transformation-rule-list.component.scss'],
})
export class TransformationRuleListComponent implements OnInit {
  private readonly rulesService = inject(TransformationRulesService);
  private readonly catalogService = inject(MappingCatalogService);
  private readonly toast = inject(ToastService);
  private readonly deIdentificationProfileSvc = inject(DeIdentificationProfileService);

  readonly scopeOptions = BROAD_SCOPE_OPTIONS;
  readonly destinationTypeOptions = DESTINATION_TYPE_OPTIONS;
  readonly resourceTypeOptions = ['Patient', 'Observation', 'Encounter', 'Condition', 'Practitioner'];
  readonly preMappingHashingMaskingModes = PRE_MAPPING_HASHING_MASKING_MODES;

  readonly deIdentificationProfiles = signal<DeIdentificationProfileDto[]>([]);
  readonly newProfileName = signal('');
  readonly creatingProfile = signal(false);

  private loadDeIdentificationProfiles(): void {
    this.deIdentificationProfileSvc.list().subscribe({
      next: profiles => this.deIdentificationProfiles.set(profiles),
      error: () => this.deIdentificationProfiles.set([]),
    });
  }

  profileName(id: string | null): string {
    if (!id) return 'None';
    return this.deIdentificationProfiles().find(p => p.id === id)?.name ?? 'None';
  }

  createDeIdentificationProfileForNewTarget(): void {
    const name = this.newProfileName().trim();
    if (!name) return;
    this.creatingProfile.set(true);
    this.deIdentificationProfileSvc.create({ name }).subscribe({
      next: profile => {
        this.deIdentificationProfiles.update(existing => [...existing, profile]);
        this.newTarget.set({ ...this.newTarget(), deIdentificationProfileId: profile.id });
        this.newProfileName.set('');
        this.creatingProfile.set(false);
      },
      error: err => {
        this.creatingProfile.set(false);
        const msg = err?.error?.title ?? err?.error?.error ?? err?.message ?? 'Failed to create the profile.';
        this.toast.error(typeof msg === 'string' ? msg : 'Failed to create the profile.');
      },
    });
  }

  setNewTargetExecutionPhase(phase: TransformExecutionPhase): void {
    this.newTarget.set({ ...this.newTarget(), executionPhase: phase, deIdentificationProfileId: phase === 'PreMapping' ? this.newTarget().deIdentificationProfileId : '' });
  }

  setNewTargetDeIdentificationProfileId(id: string): void {
    this.newTarget.set({ ...this.newTarget(), deIdentificationProfileId: id });
  }

  togglePreview(group: RuleTargetGroup): void {
    group.previewOpen = !group.previewOpen;
    if (group.previewOpen && group.previewResourceType === undefined) {
      group.previewResourceType = group.resourceType ?? '';
      group.previewSampleJson = '';
      group.previewResultJson = null;
    }
    this.groups.set([...this.groups()]);
  }

  runPreview(group: RuleTargetGroup): void {
    const profileId = group.deIdentificationProfileId;
    const resourceType = (group.previewResourceType ?? '').trim();
    const sampleJson = group.previewSampleJson ?? '';
    if (!profileId || !resourceType || !sampleJson.trim()) {
      this.toast.error('Resource type and a sample resource are both required to preview.');
      return;
    }

    group.previewRunning = true;
    this.groups.set([...this.groups()]);
    this.deIdentificationProfileSvc.preview(profileId, resourceType, sampleJson).subscribe({
      next: result => {
        group.previewRunning = false;
        group.previewResultJson = result.redactedJson;
        this.groups.set([...this.groups()]);
      },
      error: err => {
        group.previewRunning = false;
        this.groups.set([...this.groups()]);
        const msg = err?.error?.title ?? err?.error?.error ?? err?.message ?? 'Preview failed.';
        this.toast.error(typeof msg === 'string' ? msg : 'Preview failed.');
      },
    });
  }

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
    this.loadDeIdentificationProfiles();
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
              destinationField: r.destinationField ?? null, sourceField: r.sourceField ?? null,
              executionPhase: r.executionPhase ?? 'PostMapping', deIdentificationProfileId: r.deIdentificationProfileId ?? null,
              steps: [], editing: false,
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
    if (t.executionPhase === 'PreMapping') {
      if (t.scope !== 'Global' && t.scope !== 'ResourceType') {
        this.toast.error('Before-mapping (de-identification) rules are only valid at Global or Resource type scope.');
        return;
      }
      if (!t.sourceField.trim()) {
        this.toast.error('A source FHIR path (e.g. "agent.who.display") is required for a before-mapping rule.');
        return;
      }
      if (!t.deIdentificationProfileId) {
        this.toast.error('A de-identification profile is required for a before-mapping rule.');
        return;
      }
    }

    const key = groupKey({
      scope: t.scope, resourceType: t.resourceType, destinationType: t.destinationType || null,
      destinationField: t.destinationField, sourceField: t.sourceField,
      executionPhase: t.executionPhase, deIdentificationProfileId: t.deIdentificationProfileId || null,
    });

    // A target with this exact (scope, resourceType, destinationType, destinationField, sourceField)
    // combination already exists — load() groups rows by this same key, so creating another one here
    // wouldn't add a genuinely separate rule, just a second card that looks identical to this one until
    // the next reload folds them back into a single group's step list. Open the real one instead of
    // silently duplicating it.
    const existing = this.groups().find(g => g.key === key);
    if (existing) {
      existing.editing = true;
      this.groups.set([...this.groups()]);
      this.creatingNew.set(false);
      this.toast.info('Target already exists', 'A rule target for this exact scope already exists — use "Add another step" on it instead of creating a duplicate.');
      return;
    }

    // Pre-mapping rules always use HashingMasking — it's the only node type with a schema offering the
    // remove/hash/mask/redact/generalize vocabulary SafeHarborDeIdentificationService interprets.
    const applicable = t.sourceField ? getApplicableNodeTypes(t.sourceField, null) : ALL_NODE_TYPE_OPTIONS;
    const nodeType: TransformNodeType = t.executionPhase === 'PreMapping'
      ? 'HashingMasking'
      : applicable[0]?.value ?? ALL_NODE_TYPE_OPTIONS[0].value;
    const initialConfig = t.executionPhase === 'PreMapping'
      ? { mode: 'remove' }
      : applyNodeDefaults(this.schemaFor(nodeType), {});
    const group: RuleTargetGroup = {
      key,
      scope: t.scope,
      resourceType: t.scope === 'ResourceType' ? t.resourceType.trim() : null,
      destinationType: t.scope === 'DestinationType' ? (t.destinationType || null) : null,
      destinationField: t.executionPhase === 'PreMapping' ? null : (t.destinationField.trim() || null),
      sourceField: t.sourceField || null,
      executionPhase: t.executionPhase,
      deIdentificationProfileId: t.executionPhase === 'PreMapping' ? t.deIdentificationProfileId || null : null,
      steps: [{
        id: null, nodeType, config: initialConfig, order: 0, saving: false,
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
    const nodeType: TransformNodeType = group.executionPhase === 'PreMapping'
      ? 'HashingMasking'
      : applicable[0]?.value ?? ALL_NODE_TYPE_OPTIONS[0].value;
    const config = group.executionPhase === 'PreMapping' ? { mode: 'remove' } : applyNodeDefaults(this.schemaFor(nodeType), {});
    group.steps.push({
      id: null, nodeType, config, order: group.steps.length, saving: false,
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
      executionPhase: group.executionPhase,
      deIdentificationProfileId: group.deIdentificationProfileId,
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
