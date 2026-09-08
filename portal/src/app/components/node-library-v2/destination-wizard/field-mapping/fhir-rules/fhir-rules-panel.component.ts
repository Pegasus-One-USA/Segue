import { Component, EventEmitter, Input, OnInit, Output, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';

import {
  DestinationTypeV2 as DestinationType,
  DeIdentificationProfileDto,
} from '../../../../../models/destination-configuration-v2.model';
import { MappingCatalogService, FhirElement } from '../../../../../services/mapping-catalog.service';
import { TransformationRulesService, TransformationRule } from '../transformation-rules.service';
import { FmTreeNode, buildForest, flattenLeaves } from '../field-mapping-tree.util';
import type { ResourceFieldDef } from '../../destination-wizard.component';
import {
  FhirRulePopoverComponent, FhirRulePopoverContext, FhirRuleKind, FhirRulePathOption,
} from './fhir-rule-popover.component';
import { FhirPayloadTreeComponent } from './fhir-payload-tree.component';
import { DeIdentificationProfileService } from '../../../../../destination-connections/services/deidentification-profile.service';
import { PermissionActionGuard } from '../../../../../auth/services/permission-action-guard.service';
import { ToastService } from '../../../../../services/toast.service';

export interface FhirRulesPanelData {
  /** Resource types this workflow pulls — one tree per type. */
  resourceTypes: string[];
  destinationType: DestinationType;
  sourceSystem: string | null;
  sourceConnectionId?: string | null;
  sourceVendor?: string | null;
  /** De-identification policies to choose between; creating one stays in the wizard's own Step 1. */
  deIdentificationProfiles: DeIdentificationProfileDto[];
  selectedDeIdentificationProfileId: string | null;
  /** The workflow being edited. Transformation rules are scoped to it, so a workflow that has not been saved
   *  yet (null) has no id to key them against and authoring is blocked until it does. */
  workflowId: string | null;
}

/**
 * Transformation and de-identification rules for a FHIR-native destination, laid out like the SQL mapping
 * screen with the destination half removed: a filterable source tree on the left, and the rules already
 * attached listed under their own tab on the right. A rule is created by picking a path in the tree, which
 * opens the popover — never by an "add row" button, so a rule can't exist without a real path behind it.
 *
 * Deliberately its own component rather than a mode on the SQL canvas/list: those are built around a
 * MappingRow with a destination column, which a FHIR destination simply does not have.
 */
@Component({
  selector: 'app-fhir-rules-panel',
  standalone: true,
  imports: [CommonModule, FormsModule, FhirRulePopoverComponent, FhirPayloadTreeComponent],
  templateUrl: './fhir-rules-panel.component.html',
  styleUrls: ['./fhir-rules-panel.component.scss'],
})
export class FhirRulesPanelComponent implements OnInit {
  @Input({ required: true }) data!: FhirRulesPanelData;
  @Output() readonly closed = new EventEmitter<void>();
  /** Raised when the user picks a different de-identification policy, so the wizard's own Step 1 selection
   *  and this panel never disagree about which policy is active. */
  @Output() readonly deIdentificationProfileChange = new EventEmitter<string | null>();

  private readonly catalog = inject(MappingCatalogService);
  private readonly rulesService = inject(TransformationRulesService);
  private readonly profileService = inject(DeIdentificationProfileService);
  private readonly toast = inject(ToastService);
  private readonly actionGuard = inject(PermissionActionGuard);

  readonly activeTab = signal<FhirRuleKind>('transformation');
  readonly activeResource = signal('');
  readonly collapsedIds = signal<Set<string>>(new Set());
  /** Collapsed by default, like the mapping canvas's own Mapping list — the payload tree is the primary
   *  surface, and the list is what you open to review or edit what is already attached. */
  readonly listCollapsed = signal(true);

  readonly fieldsByResource = signal<Record<string, ResourceFieldDef[]>>({});
  readonly transformationRules = signal<TransformationRule[] | undefined>(undefined);
  readonly deIdRules = signal<TransformationRule[] | undefined>(undefined);
  readonly selectedProfileId = signal<string | null>(null);

  readonly popover = signal<FhirRulePopoverContext | null>(null);
  /** Which row's actions menu is open, by rule id — only ever one at a time. */
  readonly openMenuRuleId = signal<string | null>(null);

  // ── De-identification policy creation (mirrors the SQL list's own inline create) ──────────────────
  readonly profileList = signal<DeIdentificationProfileDto[]>([]);
  readonly newProfileName = signal('');
  readonly creatingProfile = signal(false);

  readonly resourceTypes = computed(() => this.data.resourceTypes);
  /** True while this workflow has no id yet. Rules can still be authored — they are stored against no
   *  workflow (inert) and attached by the builder on the workflow's first save — so this only drives an
   *  explanatory notice, never a block. Blocking here was wrong: the SQL connector never blocked, and
   *  authoring rules while drawing a pipeline is the normal way to work. */
  readonly workflowUnsaved = computed(
    () => this.activeTab() === 'transformation' && !this.data.workflowId);
  readonly destinationLabel = computed(() => this.data.destinationType);
  readonly profiles = computed(() => this.profileList());

  /** Every selectable path across all resources — handed to the popover so "Add rule" can pick one
   *  without the tree. */
  readonly pathOptions = computed<FhirRulePathOption[]>(() =>
    this.forest().flatMap(tree =>
      flattenLeaves(tree).map(leaf => ({
        resourceType: tree.resource,
        path: leaf.id,
        label: leaf.label,
      }))));

  readonly selectedProfileName = computed(() => {
    const id = this.selectedProfileId();
    return id ? (this.profiles().find(p => p.id === id)?.name ?? 'None') : 'None';
  });

  /** One tree per resource; only the active resource's is rendered, but building all of them keeps the
   *  "already has a rule" marks correct when switching tabs without a refetch. */
  readonly forest = computed<FmTreeNode[]>(() => {
    const fields = this.fieldsByResource();
    return buildForest(this.resourceTypes(), resource => fields[resource] ?? []);
  });

  /** The active resource's tree, as a one-element forest — the payload card takes a forest, and showing
   *  one resource at a time keeps the card the same shape the mapping canvas gives it. Filtering lives
   *  inside the card itself (its own search box), so nothing is pre-filtered here. */
  readonly visibleForest = computed<FmTreeNode[]>(() => {
    const tree = this.forest().find(node => node.resource === this.activeResource());
    return tree ? [tree] : [];
  });

  /** Rules shown under whichever tab is active. */
  readonly listedRules = computed<TransformationRule[] | undefined>(() =>
    this.activeTab() === 'transformation' ? this.transformationRules() : this.deIdRules());

  /** Paths that already carry a rule under the active tab — marked in the tree so the user can see what's
   *  configured without reading the list. */
  readonly rulePaths = computed<Set<string>>(
    () => new Set((this.listedRules() ?? []).map(rule => rule.sourceField ?? '')));

  readonly transformationCount = computed(() => this.transformationRules()?.length ?? 0);
  readonly deIdCount = computed(() => this.deIdRules()?.length ?? 0);

  ngOnInit(): void {
    this.activeResource.set(this.data.resourceTypes[0] ?? '');
    this.profileList.set(this.data.deIdentificationProfiles);
    this.selectedProfileId.set(this.data.selectedDeIdentificationProfileId);
    this.loadFields();
    this.reloadRules();
  }

  private loadFields(): void {
    for (const resourceType of this.data.resourceTypes) {
      this.catalog
        .fields(resourceType, this.data.sourceConnectionId ?? null, this.data.sourceVendor ?? null)
        .subscribe({
          next: (elements: FhirElement[]) => this.fieldsByResource.update(current => ({
            ...current,
            // The mapping engine's virtual @token fields (@runId, @now, ...) only resolve inside the
            // field-mapping engine, never against raw resource JSON — so they are not offerable here.
            [resourceType]: elements
              .filter(element => !element.fhirPath.startsWith('@'))
              .map(element => ({
                label: element.label,
                path: element.fhirPath,
                sqlColumn: '',
                csvColumn: '',
                jsonPath: element.jsonPath,
                valueType: element.valueType,
                arrays: element.arrays,
                referenceTargetTypes: element.referenceTargetTypes,
              })),
          })),
          error: () => this.fieldsByResource.update(current => ({ ...current, [resourceType]: [] })),
        });
    }
  }

  reloadRules(): void {
    this.transformationRules.set(undefined);
    this.rulesService
      // Scoped to THIS workflow. Without resourcePipelineRouteId the list returned every workflow's rules for
      // the destination type, which is how a rule nobody created here showed up here.
      .list({
        destinationType: this.data.destinationType,
        executionPhase: 'FhirResource',
        resourcePipelineRouteId: this.data.workflowId ?? undefined,
      })
      .subscribe({
        next: rules => this.transformationRules.set(
          rules.filter(rule =>
            rule.resourceType
            && this.data.resourceTypes.includes(rule.resourceType)
            // Either already this workflow's, or still unattached — an unsaved workflow's own pending rules,
            // which must be visible and editable here even though nothing can run them yet.
            && (rule.resourcePipelineRouteId ?? null) === (this.data.workflowId ?? null))),
        error: () => {
          this.transformationRules.set([]);
          this.toast.error('Could not load transformation rules.');
        },
      });

    this.reloadDeIdRules();
  }

  private reloadDeIdRules(): void {
    const profileId = this.selectedProfileId();
    if (!profileId) {
      this.deIdRules.set([]);
      return;
    }

    this.deIdRules.set(undefined);
    // The list endpoint has no de-identification-profile filter, so the phase narrows it and the profile id
    // is matched client-side — the same approach the SQL list takes for this tab.
    this.rulesService.list({ executionPhase: 'PreMapping' }).subscribe({
      next: rules => this.deIdRules.set(
        rules.filter(rule => rule.deIdentificationProfileId === profileId)),
      error: () => this.deIdRules.set([]),
    });
  }

  /** Creates a policy and selects it, so the very next click in the tree attaches a rule to it — the same
   *  one-step flow the SQL list's "+ New Policy" gives. */
  createProfile(): void {
    const name = this.newProfileName().trim();
    if (!name || this.creatingProfile()) return;
    if (!this.actionGuard.ensure(
      'deidentificationprofiles.write',
      'You do not have permission to create de-identification policies.')) return;

    this.creatingProfile.set(true);
    this.profileService.create({ name }).subscribe({
      next: profile => {
        this.creatingProfile.set(false);
        this.newProfileName.set('');
        this.profileList.update(profiles => [...profiles, profile]);
        this.onProfileChange(profile.id);
        this.toast.success('Policy created', profile.name);
      },
      error: () => {
        this.creatingProfile.set(false);
        this.toast.error('Could not create the policy.');
      },
    });
  }

  onProfileChange(profileId: string): void {
    const next = profileId || null;
    this.selectedProfileId.set(next);
    this.deIdentificationProfileChange.emit(next);
    this.reloadDeIdRules();
  }

  // ── tree ────────────────────────────────────────────────────────────────────

  /** Handed to the payload card as plain functions, matching how the mapping canvas feeds its own tree.
   *  The card overrides the collapse function while its search box is active, so nothing here has to know
   *  about filtering. */
  readonly isCollapsedFn = (id: string): boolean => this.collapsedIds().has(id);

  readonly hasRuleFn = (id: string): boolean => this.rulePaths().has(id);

  toggleCollapsed(id: string): void {
    this.collapsedIds.update(current => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  }

  /** Clicking a field opens the popover for that path — creating a rule, or editing the one already on
   *  it. This is the FHIR equivalent of clicking a connector line on the mapping canvas: there is no
   *  destination column to click instead. */
  onFieldClick(node: FmTreeNode): void {
    if (node.kind !== 'leaf') return;
    if (this.activeTab() === 'deidentification' && !this.selectedProfileId()) {
      this.toast.warning('Select a de-identification policy before adding rules to it.');
      return;
    }

    const existing = (this.listedRules() ?? []).find(rule => rule.sourceField === node.id) ?? null;
    this.openPopover(node.resource, node.id, existing);
  }

  editRule(rule: TransformationRule): void {
    this.openMenuRuleId.set(null);
    this.openPopover(rule.resourceType ?? this.activeResource(), rule.sourceField ?? '', rule);
  }

  deleteRule(rule: TransformationRule): void {
    this.openMenuRuleId.set(null);
    if (!this.actionGuard.ensure(
      'transformationrules.delete',
      'You do not have permission to delete transformation rules.')) return;

    this.rulesService.delete(rule.id).subscribe({
      next: () => {
        this.toast.success('Rule removed', rule.sourceField ?? '');
        this.reloadRules();
      },
      error: () => this.toast.error('Could not remove the rule.'),
    });
  }

  toggleMenu(ruleId: string): void {
    this.openMenuRuleId.update(current => (current === ruleId ? null : ruleId));
  }

  /** Opens the popover with no path chosen yet — the "+ Add rule" entry point, which picks resource and
   *  path inside the modal rather than from the tree. */
  addRule(): void {
    if (this.activeTab() === 'deidentification' && !this.selectedProfileId()) {
      this.toast.warning('Select or create a de-identification policy first.');
      return;
    }
    this.openPopover(this.activeResource(), '', null);
  }

  private openPopover(resourceType: string, sourceField: string, existing: TransformationRule | null): void {
    this.popover.set({
      kind: this.activeTab(),
      resourceType,
      sourceField,
      pathOptions: this.pathOptions(),
      resourceTypes: this.resourceTypes(),
      destinationType: this.data.destinationType,
      sourceSystem: this.data.sourceSystem,
      deIdentificationProfileId: this.selectedProfileId(),
      workflowId: this.data.workflowId,
      existing,
    });
  }

  onPopoverApplied(): void {
    this.reloadRules();
  }

  // ── list ────────────────────────────────────────────────────────────────────

  /** Where a transformation rule's output lands — the write-back path, or the read path when unset. */
  writesTo(rule: TransformationRule): string {
    const explicit = rule.fhirWriteBackJsonPath?.trim();
    if (explicit) return explicit;
    const prefix = `${rule.resourceType}.`;
    const source = rule.sourceField ?? '';
    return source.startsWith(prefix) ? source.slice(prefix.length) : source;
  }

  configSummary(rule: TransformationRule): string {
    const entries = Object.entries(rule.config ?? {});
    if (!entries.length) return '—';
    return entries.map(([key, value]) => `${key}=${value}`).join(', ');
  }

  close(): void {
    this.closed.emit();
  }
}
