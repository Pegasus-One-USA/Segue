import { Component, EventEmitter, Input, OnInit, Output, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';

import {
  TransformationRulesService, TransformationRule, TransformNodeType, TransformNodeSchema,
  NullPolicy, TransformErrorPolicy, TransformArrayMode, TransformExecutionPhase,
} from '../transformation-rules.service';
import { DestinationTypeV2 as DestinationType } from '../../../../../models/destination-configuration-v2.model';
import { ALL_NODE_TYPE_OPTIONS } from '../transform-node-classifier';
import { RuleConfigFormComponent, applyNodeDefaults } from '../rule-config-form/rule-config-form.component';
import { ToastService } from '../../../../../services/toast.service';
import { PermissionActionGuard } from '../../../../../auth/services/permission-action-guard.service';

/** What the popover is being opened for — decides which phase the rule is saved in and which fields the
 *  form offers. Transformation rules read and write FHIR paths; de-identification rules redact one in place
 *  and belong to a policy. */
export type FhirRuleKind = 'transformation' | 'deidentification';

/** One selectable FHIR path, for the popover's own field picker. */
export interface FhirRulePathOption {
  resourceType: string;
  /** Resource-qualified path, e.g. "Observation.valueQuantity.value". */
  path: string;
  label: string;
}

export interface FhirRulePopoverContext {
  kind: FhirRuleKind;
  resourceType: string;
  /** Resource-qualified FHIR path, e.g. "Observation.valueQuantity.value". Empty when the popover was
   *  opened from "Add rule" rather than by clicking a path — the picker below then chooses it. */
  sourceField: string;
  /** Every path the user may pick from, grouped by resource. Only consulted while sourceField is empty. */
  pathOptions: FhirRulePathOption[];
  /** Resource types offered by the picker, in the order the panel shows them. */
  resourceTypes: string[];
  destinationType: DestinationType;
  sourceSystem: string | null;
  /** Required for a de-identification rule, ignored for a transformation rule. */
  deIdentificationProfileId?: string | null;
  /** The workflow these rules belong to. A transformation rule is saved against it, so it applies to THIS
   *  pipeline and no other — null means the workflow has not been saved yet and nothing can be authored. */
  workflowId: string | null;
  /** The rule being edited, or null to create a new one. */
  existing: TransformationRule | null;
}

/**
 * The FHIR counterpart of the mapping canvas's join popover: one path, one rule, configured in a modal and
 * applied. Deliberately separate from FieldMappingJoinPopoverComponent rather than a mode on it — that one is
 * built around a MappingRow (sources plus a destination column), and a FHIR destination has no column half at
 * all, so sharing would mean threading "no destination" through every one of its branches.
 */
@Component({
  selector: 'app-fhir-rule-popover',
  standalone: true,
  imports: [CommonModule, FormsModule, RuleConfigFormComponent],
  templateUrl: './fhir-rule-popover.component.html',
  styleUrls: ['./fhir-rule-popover.component.scss'],
})
export class FhirRulePopoverComponent implements OnInit {
  @Input({ required: true }) context!: FhirRulePopoverContext;
  /** Emitted after a successful save or delete, so the list behind the popover can refresh. */
  @Output() readonly applied = new EventEmitter<void>();
  @Output() readonly closed = new EventEmitter<void>();

  private readonly rulesService = inject(TransformationRulesService);
  private readonly toast = inject(ToastService);
  private readonly actionGuard = inject(PermissionActionGuard);

  readonly nodeTypeOptions = ALL_NODE_TYPE_OPTIONS;
  readonly schemas = signal<TransformNodeSchema[]>([]);
  readonly saving = signal(false);

  readonly nodeType = signal<TransformNodeType>('StringNormalization');
  readonly config = signal<Record<string, string>>({});
  readonly writeBackPath = signal('');
  readonly onNull = signal<NullPolicy>('Skip');
  readonly onNullDefaultValue = signal('');
  readonly errorPolicy = signal<TransformErrorPolicy>('NullOut');
  readonly arrayMode = signal<TransformArrayMode>('Whole');
  readonly isEnabled = signal(true);

  /** Chosen in the popover when it was opened without one. Seeded from the context in ngOnInit. */
  readonly resourceType = signal('');
  readonly sourceField = signal('');

  readonly isDeIdentification = computed(() => this.context.kind === 'deidentification');
  readonly isEditing = computed(() => !!this.context.existing);
  /** True when the path was not decided before opening, so the picker is shown. */
  readonly picksPath = computed(() => !this.context.sourceField && !this.context.existing);

  readonly pathOptionsForResource = computed(() =>
    this.context.pathOptions.filter(option => option.resourceType === this.resourceType()));

  /** Node types whose output replaces a whole element rather than a leaf value, so the write-back path
   *  usually needs to be the PARENT of the path read. Drives a hint, never a hard rule. */
  private static readonly STRUCTURE_BUILDING = new Set<TransformNodeType>([
    'CodeableConceptBuilder', 'ReferenceConstruction', 'QuantityRangeAssembly',
    'HumanNameParsing', 'AddressParsing',
  ]);

  readonly warnsAboutWriteBack = computed(() =>
    !this.isDeIdentification()
    && FhirRulePopoverComponent.STRUCTURE_BUILDING.has(this.nodeType())
    && !this.writeBackPath().trim());

  /** The path this rule's output actually lands on. The read path is resource-qualified; the write-back path
   *  is not, so the prefix is stripped for the default. */
  readonly effectiveWriteBackPath = computed(() => {
    const explicit = this.writeBackPath().trim();
    if (explicit) return explicit;
    const prefix = `${this.resourceType()}.`;
    const source = this.sourceField();
    return source.startsWith(prefix) ? source.slice(prefix.length) : source;
  });

  ngOnInit(): void {
    this.resourceType.set(this.context.existing?.resourceType ?? this.context.resourceType);
    this.sourceField.set(this.context.existing?.sourceField ?? this.context.sourceField);

    this.rulesService.getNodeSchemas().subscribe({
      next: schemas => {
        this.schemas.set(schemas);
        // Defaults can only be applied once the schema is known, so a brand-new rule fills in here rather
        // than at construction.
        if (!this.context.existing) this.config.set(applyNodeDefaults(this.schemaFor(this.nodeType()), {}));
      },
      error: () => this.schemas.set([]),
    });

    const existing = this.context.existing;
    if (existing) {
      this.nodeType.set(existing.nodeType);
      this.config.set({ ...existing.config });
      this.writeBackPath.set(existing.fhirWriteBackJsonPath ?? '');
      this.onNull.set(existing.onNull);
      this.onNullDefaultValue.set(existing.onNullDefaultValue ?? '');
      this.errorPolicy.set(existing.errorPolicy);
      this.arrayMode.set(existing.arrayMode);
      this.isEnabled.set(existing.isEnabled);
      return;
    }

    // A de-identification rule is always a redaction, so it opens on the only node type that performs one
    // rather than making the user find it in a list of twenty.
    if (this.isDeIdentification()) this.nodeType.set('HashingMasking');
  }

  schemaFor(nodeType: TransformNodeType): TransformNodeSchema | undefined {
    return this.schemas().find(schema => schema.nodeType === nodeType);
  }

  /** Switching resource invalidates the chosen path — it belonged to the previous resource's tree. */
  onResourceChange(resourceType: string): void {
    this.resourceType.set(resourceType);
    this.sourceField.set('');
  }

  onNodeTypeChange(nodeType: TransformNodeType): void {
    this.nodeType.set(nodeType);
    // The previous node's config keys mean nothing to the new one — carrying them over leaves orphaned
    // values the author can't see and the node ignores.
    this.config.set(applyNodeDefaults(this.schemaFor(nodeType), {}));
  }

  apply(): void {
    if (!this.actionGuard.ensure(
      'transformationrules.write',
      'You do not have permission to modify transformation rules.')) return;

    const context = this.context;
    if (context.kind === 'deidentification' && !context.deIdentificationProfileId) {
      this.toast.error('Select a de-identification policy first.');
      return;
    }

    if (!this.sourceField().trim()) {
      this.toast.error('Pick the FHIR path this rule applies to.');
      return;
    }

    this.saving.set(true);

    const phase: TransformExecutionPhase =
      context.kind === 'deidentification' ? 'PreMapping' : 'FhirResource';

    this.rulesService.save({
      id: context.existing?.id ?? null,
      // A transformation rule belongs to ONE workflow — Workflow scope, keyed by the workflow id below.
      // Anything broader (Field/ResourceType/...) applies to every pipeline sharing the destination type,
      // which is how a rule authored in one workflow used to start transforming another's resources.
      // De-identification rules stay ResourceType-scoped: they belong to a POLICY, which is itself the
      // reusable unit, and a policy is chosen per destination.
      scope: context.kind === 'transformation' ? 'Workflow' : 'ResourceType',
      // Null while the workflow is unsaved. The rule is stored anyway and is inert until the builder attaches
      // it on the workflow's first save (see /transformation-rules/attach-pending) — the resolver matches this
      // id exactly, so an unattached rule can never apply to any run.
      resourcePipelineRouteId: context.kind === 'transformation' ? context.workflowId : null,
      nodeType: this.nodeType(),
      config: this.config(),
      // A pre-mapping rule belongs to its policy, not to a destination — sending a destination type would
      // make it look like a destination-scoped rule to the resolver.
      destinationType: context.kind === 'deidentification' ? null : context.destinationType,
      resourceType: this.resourceType(),
      destinationField: null,
      sourceSystem: context.kind === 'deidentification' ? null : context.sourceSystem,
      sourceField: this.sourceField().trim(),
      onNull: this.onNull(),
      onNullDefaultValue: this.onNullDefaultValue().trim() || null,
      errorPolicy: this.errorPolicy(),
      arrayMode: this.arrayMode(),
      fhirWriteBackJsonPath:
        context.kind === 'deidentification' ? null : (this.writeBackPath().trim() || null),
      isEnabled: this.isEnabled(),
      executionPhase: phase,
      deIdentificationProfileId:
        context.kind === 'deidentification' ? context.deIdentificationProfileId! : null,
    }).subscribe({
      next: () => {
        this.saving.set(false);
        this.toast.success(this.isEditing() ? 'Rule updated' : 'Rule applied', this.sourceField());
        this.applied.emit();
        this.closed.emit();
      },
      error: () => {
        this.saving.set(false);
        this.toast.error('Could not save the rule.');
      },
    });
  }

  remove(): void {
    const existing = this.context.existing;
    if (!existing) return;
    if (!this.actionGuard.ensure(
      'transformationrules.delete',
      'You do not have permission to delete transformation rules.')) return;

    this.saving.set(true);
    this.rulesService.delete(existing.id).subscribe({
      next: () => {
        this.saving.set(false);
        this.toast.success('Rule removed');
        this.applied.emit();
        this.closed.emit();
      },
      error: () => {
        this.saving.set(false);
        this.toast.error('Could not remove the rule.');
      },
    });
  }

  close(): void {
    this.closed.emit();
  }
}
