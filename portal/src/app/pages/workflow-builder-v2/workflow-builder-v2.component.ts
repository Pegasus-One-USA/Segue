import { Component, ElementRef, OnInit, inject, signal, computed, viewChild } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Observable, Subject } from 'rxjs';
import { PermissionService } from '../../auth/services/permission.service';
import { HasUnsavedChanges } from '../../core/guards/has-unsaved-changes';
import { UnsavedChangesRegistryService } from '../../core/services/unsaved-changes-registry.service';
import { PipelineStoreV2 } from '../../services/pipeline-v2.store';
import { ToastService } from '../../services/toast.service';
import { ApplicabilityServiceV2 } from '../../services/applicability-v2.service';
import { WorkflowApiService, WorkflowBuildRequest, WorkflowBuildResult, WorkflowTriggerRequest } from '../../services/workflow-api.service';
import { WorkflowGraphMapperServiceV2 } from '../../services/workflow-graph-mapper-v2.service';
import { WorkflowBuildAssemblerServiceV2 } from '../../services/workflow-build-assembler-v2.service';
import { SOURCES } from '../../data/sources-v2.data';
import { TRANSFORMS } from '../../data/transforms-v2.data';
import { SQL_FAMILY_DESTINATION_TYPES } from '../../models/transform-v2.model';

import { TransformationRulesService } from '../../components/node-library-v2/destination-wizard/field-mapping/transformation-rules.service';
import { Source } from '../../models/source.model';
import { CanvasNode, SourceNode, TransformNode, MergeNode, isSourceNode } from '../../models/node-v2.model';
import { environment } from '../../../environments/environment';
/** V2's chain steps in canonical canvas order — Source → Mapping → Transformation →
 *  De-identification → Destination. Mirrors ApplicabilityServiceV2.CHAIN_STEP_IDS. */
const CHAIN_STEP_IDS: string[] = ['field-mapping', 'transformation', 'deidentification'];

import { CanvasComponent } from '../../components/canvas-v2/canvas.component';
import { PayloadPreviewComponent } from '../../components/modals-v2/payload-preview/payload-preview.component';
import {
  NodeLibraryDialogComponent,
  LibraryMode,
  AddTransformEvent,
  MergeEvent,
} from '../../components/node-library-v2/node-library-dialog.component';

@Component({
  selector: 'app-workflow-builder-v2',
  standalone: true,
  imports: [
    CanvasComponent,
    PayloadPreviewComponent,
    NodeLibraryDialogComponent,
  ],
  templateUrl: './workflow-builder-v2.component.html',
  styleUrl: './workflow-builder-v2.component.scss',
})
export class WorkflowBuilderV2Component implements OnInit, HasUnsavedChanges {
  private readonly store  = inject(PipelineStoreV2);
  private readonly toast  = inject(ToastService);
  private readonly appSvc = inject(ApplicabilityServiceV2);
  private readonly workflowApi = inject(WorkflowApiService);
  private readonly graphMapper = inject(WorkflowGraphMapperServiceV2);
  private readonly buildAssembler = inject(WorkflowBuildAssemblerServiceV2);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly permissions = inject(PermissionService);
  private readonly unsavedChangesRegistry = inject(UnsavedChangesRegistryService);
  private readonly transformationRules = inject(TransformationRulesService);

  constructor() {
    this.unsavedChangesRegistry.register(() => this.hasUnsavedChanges() || this.isSaveInProgress());
  }

  // ── page state ─────────────────────────────────────────────────────────────
  protected readonly scenarioName = signal('Grouped-node pipeline (Normalize group + merge)');

  // ── RBAC: view vs. mutate ───────────────────────────────────────────────────
  // workflow.view only grants VIEW access — reaching this page and loading an existing workflow onto the
  // canvas. Actually changing anything (adding/editing/deleting a module, Save) needs the action-specific
  // permission: workflow.create while building a brand-new workflow (no ?id=), workflow.edit once editing
  // one that already exists — mirrors exactly the branch WorkflowEndpoints' POST /workflows/build now
  // checks server-side, so the UI and the backend can never disagree about which permission a given save
  // needs. Set synchronously from the route's ?id= in ngOnInit (not derived from currentWorkflowId, which
  // stays null until the async load resolves) so canMutate is correct from the very first render.
  protected readonly isEditingExistingWorkflow = signal(false);
  protected readonly canMutate = computed(() =>
    this.isEditingExistingWorkflow()
      ? this.permissions.hasPermission('workflow.edit')
      : this.permissions.hasPermission('workflow.create'));

  // ── node library dialog (unified — replaces source + transform pickers) ────
  protected readonly libraryOpen      = signal(false);
  protected readonly libraryMode      = signal<LibraryMode>('source');
  protected readonly libraryOriginId  = signal<string | null>(null);
  protected readonly editingNodeId    = signal<string | null>(null);

  // ── other modals ───────────────────────────────────────────────────────────
  protected readonly payloadOpen  = signal(false);
  protected readonly confirmReset = signal(false);
  protected readonly currentWorkflowId = signal<string | null>(null);
  protected readonly workflowName = signal('');
  // True once the name field has been blurred or a save was attempted while empty — gates the invalid
  // (red border + inline message) state so it doesn't show before the user has had a chance to type.
  protected readonly nameTouched = signal(false);
  protected readonly nameInvalid = computed(() => this.nameTouched() && !this.workflowName().trim());
  private readonly nameInput = viewChild<ElementRef<HTMLInputElement>>('nameInput');
  protected readonly workflowIdInput = signal('');
  protected readonly workflowBusy = signal(false);
  protected readonly workflowStatus = signal('Catalog loading...');

  // ── Trigger / Scheduler (Backend-Systems workflows only) ────────────────────
  // Shown when a source node is configured for the Backend Systems audience — those run headless on a schedule
  // rather than being launched. Compiled into the workflow's trigger metadata (approach B) on save.
  protected readonly triggerType = signal<'Manual' | 'Daily' | 'Weekly' | 'Monthly' | 'Cron' | 'Poll'>('Manual');
  protected readonly cronExpression = signal('0 0 * * *');
  protected readonly pollMinutes = signal(15);

  /** True when any source node targets the Backend Systems audience — enables the Trigger control. */
  protected readonly isBackendAudience = computed(() =>
    this.store.nodes()
      .filter(isSourceNode)
      .some(node => {
        const fields = node.fields ?? {};
        const audience = (fields['Epic audience'] || fields['App key'] || '').toLowerCase();
        return audience.includes('backend');
      }));

  /**
   * A Backend System source configured through the Search REST retrieval wizard carries its own Run Mode +
   * Schedule/Poll Frequency (or Full Refresh calendar recurrence) — that config IS the trigger for this workflow,
   * so it supersedes the manual toolbar control below. Falls back to `null` (toolbar-driven) for anything that
   * hasn't gone through that wizard step, so older/other workflows keep behaving exactly as before.
   */
  private readonly backendRetrievalFields = computed(() =>
    this.store.nodes()
      .filter(isSourceNode)
      .map(node => node.fields ?? {})
      // Search-REST carries a Run mode; bulk export carries an Export scope. Either means the wizard already
      // describes how this pipeline runs, so it drives the trigger instead of the manual toolbar control.
      .find(fields => !!fields['Retrieval method key']
        && (!!fields['Run mode'] || fields['Retrieval method key'] === 'bulk-export')) ?? null);

  protected readonly isWizardDrivenTrigger = computed(() => this.backendRetrievalFields() !== null);

  /** Human-readable summary of the wizard-derived trigger, shown in place of the manual toolbar control. */
  protected readonly wizardTriggerSummary = computed(() => {
    const fields = this.backendRetrievalFields();
    if (!fields) return '';
    if (fields['Retrieval method key'] === 'bulk-export') {
      const scope = fields['Export scope'] || 'system';
      if (scope === 'patient') return 'Bulk Export (Patient list) — runs on demand';
      return `Bulk Export (${scope}) — ${fields['Full refresh schedule (cron)'] || 'schedule pending'}`;
    }
    switch (fields['Run mode']) {
      case 'incremental': return `Incremental Sync — ${this.pollFrequencyLabel(fields['Schedule / poll frequency'])}`;
      case 'full':         return `Full Refresh — ${fields['Full refresh schedule (cron)'] || 'schedule pending'}`;
      default:              return 'Manual Only — runs on demand';
    }
  });

  private pollFrequencyLabel(raw: string | undefined): string {
    const labels: Record<string, string> = { '5m': 'every 5 min', '15m': 'every 15 min', '30m': 'every 30 min', '1h': 'hourly', '1d': 'daily' };
    return raw ? (labels[raw] ?? raw) : 'frequency pending';
  }

  private pollFrequencyToMinutes(raw: string | undefined): number {
    const minutes: Record<string, number> = { '5m': 5, '15m': 15, '30m': 30, '1h': 60, '1d': 1440 };
    return raw ? (minutes[raw] ?? 15) : 15;
  }

  onTriggerTypeInput(value: string): void {
    this.triggerType.set(value as 'Manual' | 'Daily' | 'Weekly' | 'Monthly' | 'Cron' | 'Poll');
  }
  onCronInput(value: string): void { this.cronExpression.set(value); }
  onPollMinutesInput(value: string): void { this.pollMinutes.set(Math.max(1, Number(value) || 1)); }

  /**
   * Compiles the workflow's trigger DTO. A Backend System source configured via the Search REST retrieval wizard
   * (Run Mode + Schedule/Poll Frequency or Full Refresh calendar recurrence) takes priority — that config already
   * says exactly how this pipeline should run, so re-deriving the trigger from it avoids the wizard and the toolbar
   * silently disagreeing about the same workflow's schedule. Falls back to the manual toolbar control for anything
   * that hasn't gone through that wizard step (including non-backend workflows, which never schedule → null).
   */
  private buildTrigger(): WorkflowTriggerRequest | null {
    const wizardFields = this.backendRetrievalFields();
    if (wizardFields) {
      // Bulk export: System/Group run on the calendar Repeat (compiled cron); a Patient id list is a one-off (manual).
      const timeZoneId = wizardFields['Full refresh time zone'] || 'UTC';
      if (wizardFields['Retrieval method key'] === 'bulk-export') {
        return wizardFields['Export scope'] === 'patient'
          ? { type: 'Manual' }
          : { type: 'Schedule', scheduleExpression: wizardFields['Full refresh schedule (cron)'] || '0 2 * * *', timeZoneId };
      }
      switch (wizardFields['Run mode']) {
        case 'incremental':
          return { type: 'Poll', intervalMinutes: this.pollFrequencyToMinutes(wizardFields['Schedule / poll frequency']) };
        case 'full':
          return { type: 'Schedule', scheduleExpression: wizardFields['Full refresh schedule (cron)'] || '0 2 * * *', timeZoneId };
        default:
          return { type: 'Manual' };
      }
    }

    if (!this.isBackendAudience()) {
      return null;
    }
    switch (this.triggerType()) {
      case 'Daily':   return { type: 'Schedule', scheduleExpression: '0 0 * * *' };
      case 'Weekly':  return { type: 'Schedule', scheduleExpression: '0 0 * * 0' };
      case 'Monthly': return { type: 'Schedule', scheduleExpression: '0 0 1 * *' };
      case 'Cron':    return { type: 'Schedule', scheduleExpression: this.cronExpression().trim() };
      case 'Poll':    return { type: 'Poll', intervalMinutes: this.pollMinutes() };
      default:        return { type: 'Manual' };
    }
  }

  ngOnInit(): void {
    // Deep-link from the Workflow List "Edit" action: ?id=<workflowId> loads that graph onto the canvas after the
    // catalog resolves (the mapper needs node metadata), so Save issues a PUT update of the same workflow.
    const editId = this.route.snapshot.queryParamMap.get('id');
    this.isEditingExistingWorkflow.set(!!editId);

    // The PipelineStoreV2 is a root singleton, so its canvas state outlives this component (e.g. an abandoned,
    // unsaved edit/creation left nodes on it). A fresh "New Workflow" navigation must always start blank rather
    // than inheriting whatever was left over from whatever was on the canvas before.
    if (!editId) {
      this.resetCanvasAndWorkflowState();
    }

    this.workflowApi.loadCatalog().subscribe({
      next: items => {
        this.workflowStatus.set(`Catalog loaded (${items.length} nodes).`);
        if (editId) {
          this.workflowIdInput.set(editId);
          this.onLoadWorkflow();
        }
      },
      error: () => this.workflowStatus.set('Catalog could not be loaded. Save is disabled.'),
    });
  }

  // ── topbar ─────────────────────────────────────────────────────────────────
  onReset(): void {
    // Defense in depth — the trigger button is already hidden whenever !canMutate() (see the
    // template), same rule as Save: workflow.create for a new workflow, workflow.edit for an
    // existing one.
    if (!this.canMutate()) { this.confirmReset.set(false); return; }
    this.store.reset();
    this.toast.show('Canvas reset', 'All nodes removed.');
    this.confirmReset.set(false);
  }

  onResetBackdropClick(e: MouseEvent): void {
    if (e.target === e.currentTarget) this.confirmReset.set(false);
  }

  onWorkflowNameInput(value: string): void {
    this.workflowName.set(value);
  }

  onWorkflowNameBlur(): void {
    this.nameTouched.set(true);
  }

  clearWorkflowName(): void {
    this.workflowName.set('');
    this.nameInput()?.nativeElement.focus();
  }

  /**
   * Single "Save". Behaves by context:
   * - Canvas carries wizard-drawn source/destination specs → create-on-save (POST /workflows/build). When editing an
   *   already-built workflow, the existing workflow id (and each spec's existingId, sourced from the node's own
   *   fields) is passed along so the server updates the workflow definition and its Source/Destination/MappingProfile
   *   records in place instead of duplicating them — this is what makes edits to retrieval/connection config actually
   *   reach the backing entity, not just the node's display config.
   * - Canvas has no such specs (pure picker-by-id or transform-only) → plain design save (PUT).
   * Interactive/launch workflows are saved enabled with their source binding (build enables by default; the plain-save
   * path activates when a launch source id is present).
   */
  onSave(): void {
    // Defense in depth — the Save button is already hidden whenever !canMutate() (see the template), and
    // the backend independently rejects the underlying build/save call with 403 regardless of this check.
    if (!this.canMutate()) {
      return;
    }

    if (!this.workflowName().trim()) {
      this.nameTouched.set(true);
      this.nameInput()?.nativeElement.focus();
      this.toast.error('Name required', 'Workflow name is missing — give this workflow a name before saving.');
      return;
    }

    if (this.store.nodes().length === 0) {
      this.toast.error('Nothing to save', 'Add at least one node to the canvas before saving.');
      return;
    }

    if (this.workflowApi.catalog().length === 0) {
      this.workflowStatus.set('Catalog is not loaded yet.');
      return;
    }

    const name = this.workflowName().trim();
    const existingId = this.currentWorkflowId();
    const isLaunch = !!this.graphMapper.findLaunchSourceId();

    let request: WorkflowBuildRequest;
    try {
      request = this.buildAssembler.assemble(name, this.buildTrigger());
    } catch (err) {
      // buildAssembler throws for configuration gaps it can catch up front (e.g. Upsert write mode with no
      // id-mapped key column) — surfaced here rather than round-tripping to the backend for the same rejection.
      const msg = err instanceof Error ? err.message : 'Workflow configuration is invalid.';
      this.workflowStatus.set(msg);
      this.toast.error('Cannot save workflow', msg);
      return;
    }
    // Mappings must count too: a workflow wired entirely to already-provisioned source/destination
    // connections (sourceConnectionResolved/destinationResolved both "true") has zero source/destination
    // specs to create, but can still carry new/changed mapping rows that need a MappingProfile created and
    // stamped onto the Field Mapping node — skipping the build call in that case silently left the mapping
    // node's config empty (no mappingProfileId), so every run mapped zero records despite "succeeding".
    const hasSpecs = (request.sources?.length ?? 0) > 0
      || (request.destinations?.length ?? 0) > 0
      || (request.mappings?.length ?? 0) > 0;
    // Belt-and-suspenders: assemble() already includes any unresolved source/destination in hasSpecs, but the
    // plain design save (saveWorkflow → PUT /workflows/{id}) never provisions secrets at all — it just serializes
    // whatever's on the canvas as-is. If a node is still carrying a raw, unresolved secret for any reason (e.g. a
    // future gap in assemble()'s own detection), routing it to the plain save would silently persist the secret
    // in plaintext node config while leaving the backing connection's Key Vault reference untouched. Never let
    // that happen — force the provisioning path whenever an unresolved secret is present, regardless of hasSpecs.
    if (hasSpecs || this.hasUnresolvedSecrets()) {
      this.buildWorkflow({ ...request, workflowId: existingId ?? undefined });
      return;
    }

    // No wizard-drawn specs → plain design save.
    this.saveWorkflow(name, existingId, isLaunch);
  }

  /** True if any canvas node holds a freshly typed secret (source Client Secret, destination password/secret/
   *  token/connection-string) whose backing connection hasn't been resolved/provisioned yet. See onSave(). */
  private hasUnresolvedSecrets(): boolean {
    const destSecretKeys = [
      'dest_password', 'dest_sftpPassword', 'dest_connectionString', 'dest_clientSecret', 'dest_bearerToken',
      'dest_blobSecret', 'dest_medplumSecret',
    ];
    return this.store.nodes().some(node => {
      const fields = node.fields ?? {};
      if (fields['sourceConnectionResolved'] !== 'true' && (fields['Client Secret'] ?? '').trim()) {
        return true;
      }
      if (fields['destinationResolved'] !== 'true' && destSecretKeys.some(key => (fields[key] ?? '').trim())) {
        return true;
      }
      return false;
    });
  }

  private buildWorkflow(request: WorkflowBuildRequest): void {
    const isUpdate = !!request.workflowId;
    this.workflowBusy.set(true);
    this.workflowStatus.set(isUpdate ? 'Syncing configs + saving workflow...' : 'Creating configs + saving workflow...');
    this.workflowApi.build(request).subscribe({
      next: result => {
        this.currentWorkflowId.set(result.workflowId);
        this.workflowIdInput.set(result.workflowId);
        const synced =
          Object.keys(result.sourceConnectionIds).length +
          Object.keys(result.destinationIds).length +
          Object.keys(result.mappingProfileIds).length;
        const unmapped = this.buildAssembler.lastUnmappedResources;
        const caveat = unmapped.length ? ` (not wired: ${unmapped.join(', ')})` : '';
        const verb = isUpdate ? 'Synced' : 'Created';
        this.workflowStatus.set(`${verb} ${synced} config(s) + saved workflow.${caveat}`);
        this.toast.success('Workflow saved', `Configs ${isUpdate ? 'synced' : 'provisioned'} and saved. You can Run it now.${caveat}`);
        this.announceSyncedScopes(result.syncedScopesBySourceConnectionId);
        this.stampBuildResultIds(result);
        this.reconcileGeneratedJwksUrls(result.workflowId, request.name, result.sourceConnectionIds);
      },
      error: err => {
        const msg = typeof err?.error?.error === 'string'
          ? err.error.error
          : 'Something went wrong while saving this workflow. Please contact your admin.';
        // A structured validation rejection (RequestValidationException / WorkflowEndpoints' ValidationBadRequest
        // helper) carries per-field messages alongside the flat `error` string — surface all of them rather than
        // just the generic top-level message, since `msg` alone ("Validation failed.") isn't actionable on its own.
        const fieldErrors = err?.error?.fieldErrors as Record<string, string[]> | null | undefined;
        const detail = fieldErrors && Object.keys(fieldErrors).length
          ? Object.values(fieldErrors).flat().join(' ')
          : msg;
        this.workflowStatus.set(detail);
        this.toast.error('Create-on-save failed', detail);
        this.workflowBusy.set(false);
      },
    });
  }

  // Surfaces what actually changed on the shared Epic connection(s) this save touched — its real scopes are
  // derived server-side from every pipeline's destination resource picks, not just this one, so a save here can
  // silently change what another pipeline's next sign-in requests. Making that visible beats leaving it invisible.
  private announceSyncedScopes(syncedScopes: Record<string, string[]> | undefined): void {
    const entries = Object.entries(syncedScopes ?? {});
    if (!entries.length) {
      return;
    }

    const resourceScopePattern = /^[a-z]+\/[A-Za-z]+\.[a-z]+$/;
    for (const [, scopes] of entries) {
      const resourceTypes = scopes
        .filter(scope => resourceScopePattern.test(scope))
        .map(scope => scope.split('/')[1].split('.')[0]);
      if (resourceTypes.length) {
        this.toast.show(
          'Epic scopes synced',
          `This connection's requested scopes now include: ${resourceTypes.join(', ')} (based on every workflow currently using it).`,
        );
      }
    }
  }

  /**
   * The "Private Key / JWKS URL" field a Backend System + JWT source shows is never actually sent to the backend
   * SourceConnection (no such column exists there) — the only durable place it CAN live is this node's own
   * ConfigurationJson, which the workflow definition already persists on every save. For a node whose signing key
   * FHIRBridge generated/imported, the real URL is only knowable once this build assigns a sourceConnectionId — so
   * it's wrong (a stale placeholder) on the save that just happened. Corrects it here, in the still-live canvas
   * node (before resetCanvasAndWorkflowState() would otherwise throw that state away), then persists the
   * correction with a plain definition save (PUT /workflows/{id} — NOT another /workflows/build) so it doesn't
   * re-touch the source/destinations/mappings just created/synced above, or re-trigger capability discovery.
   */
  private reconcileGeneratedJwksUrls(
    workflowId: string,
    workflowName: string,
    sourceConnectionIds: Record<string, string>,
  ): void {
    let anyCorrected = false;

    for (const [nodeId, sourceConnectionId] of Object.entries(sourceConnectionIds)) {
      const node = this.store.byId(nodeId);
      if (!node) continue;

      const fields = node.fields;
      const isGeneratedOrImportedBackendKey =
        fields['Auth method'] === 'jwt' &&
        fields['Epic audience'] === 'backend-system' &&
        (fields['Signing key source'] === 'gen' || fields['Signing key source'] === 'import');

      if (!isGeneratedOrImportedBackendKey) continue;

      const jwksUrl = `${environment.apiBase}/api/v1/source-connections/${sourceConnectionId}/.well-known/jwks.json`;
      if (fields['JWKS URL'] !== jwksUrl) {
        this.store.updateNode(nodeId, { fields: { ...fields, 'JWKS URL': jwksUrl } } as Partial<CanvasNode>);
        anyCorrected = true;
      }

      this.toast.show(
        `JWKS URL for "${fields['__name'] || 'this source'}"`,
        `Register this URL in Epic's app configuration: ${jwksUrl}`,
        'info',
        20000,
      );
    }

    if (!anyCorrected) {
      this.workflowBusy.set(false);
      this.finishSave();
      return;
    }

    const definitionRequest = this.graphMapper.toRequest(workflowName, this.buildTrigger());
    this.workflowApi.save(definitionRequest, workflowId).subscribe({
      next: () => {
        this.workflowBusy.set(false);
        this.finishSave();
      },
      error: () => {
        // Best-effort: the original build already succeeded and is fully durable — only this cosmetic
        // JWKS-URL correction failed to re-save. Not worth blocking or re-prompting the user over.
        this.workflowBusy.set(false);
        this.finishSave();
      },
    });
  }

  onLoadWorkflow(): void {
    const id = this.workflowIdInput();
    if (!id) return;

    this.workflowBusy.set(true);
    this.workflowStatus.set('Loading workflow...');
    this.workflowApi.load(id).subscribe({
      next: workflow => {
        this.graphMapper.loadDefinition(workflow);
        this.currentWorkflowId.set(workflow.id);
        this.workflowName.set(workflow.name);
        this.workflowStatus.set(`Loaded ${workflow.name}.`);
        this.workflowBusy.set(false);
      },
      error: () => {
        this.workflowStatus.set('Workflow load failed.');
        this.workflowBusy.set(false);
      },
    });
  }

  // ── canvas events (unchanged API — canvas.component stays untouched) ───────
  // canvas.component itself already hides the "+ Add module" FAB/context-menu entry when
  // [readOnly]="!canMutate()" (see the template), and onTransformSelected/onSourceSelected/
  // onMergeSelected below re-check canMutate() as the actual gate — every path that adds a node
  // ends in one of those three, however the dialog was opened, and the node-library dialog's own
  // "edit an existing node" flow reuses the same three (see onOpenWizard), which VIEWING a node's
  // config must still be able to reach for a workflow.view-only role. These two guards are just
  // the entry points that skip opening the dialog for a brand-new add at all.
  onOpenSourcePicker(): void {
    if (!this.canMutate()) return;
    this.libraryMode.set('source');
    this.libraryOriginId.set(null);
    this.libraryOpen.set(true);
  }

  onOpenTransformPicker(nodeId: string): void {
    if (!this.canMutate()) return;
    this.libraryMode.set('transform');
    this.libraryOriginId.set(nodeId);
    this.libraryOpen.set(true);
  }

  // ── node library dialog outputs ────────────────────────────────────────────
  // 'epic' never reaches here — node-library-dialog's addSelected() opens the Epic form
  // inline and returns before emitting sourceSelected for that id.
  //
  // canMutate() is re-checked here (not just at the dialog's open-triggers above) because this is
  // also where the dialog's "edit an existing node" submit lands (see onOpenWizard, which opens the
  // same dialog to let a view-only role look at a node's configuration) — without this check, that
  // dialog's own submit button could still write the edited fields onto the canvas even though
  // nothing that led here was itself blocked.
  onSourceSelected(id: string): void {
    if (!this.canMutate()) return;
    const src = SOURCES.find(s => s.id === id);
    if (!src) return;
    // Always adds a brand-new stub node (see addStubSource) — this path never edits an existing one,
    // so it's always the vendor's Create permission, never Edit.
    if (src.permissionPrefix && !this.permissions.hasPermission(`${src.permissionPrefix}.create`)) {
      this.toast.error('Not permitted', `You don't have permission to add a new ${src.name} source.`);
      return;
    }
    this.addStubSource(src);
  }

  onTransformSelected(e: AddTransformEvent): void {
    if (!this.canMutate()) return;
    const t = TRANSFORMS.find(x => x.id === e.transformId);
    if (!t) return;

    // Same View/Create/Edit split as onSourceSelected — editNodeId present means this is the dest
    // wizard's edit flow (Edit), absent means it's adding a brand-new destination node (Create).
    if (t.permissionPrefix) {
      const requiredAction = e.editNodeId ? 'edit' : 'create';
      if (!this.permissions.hasPermission(`${t.permissionPrefix}.${requiredAction}`)) {
        this.toast.error('Not permitted', `You don't have permission to ${requiredAction} a ${t.name} destination.`);
        return;
      }
    }

    // Editing an existing destination node's configuration (dest wizard edit flow). Merge onto the node's existing
    // fields rather than replacing them outright — server-injected machine keys (destinationId, mappingProfileId,
    // secretKeyVaultName/secretName from create-on-save) aren't surfaced as form controls here and must survive.
    if (e.editNodeId) {
      const previousFields = this.store.byId(e.editNodeId)?.fields ?? {};
      this.store.updateNode(e.editNodeId, {
        fields: { ...previousFields, '__name': t.name, ...(e.config ?? {}) },
      });
      // Names whatever the user actually opened — a chain node's configuration is stored on its
      // destination, so without chainLabel this would confusingly report "SQL Server configuration
      // updated" after saving the Mapping/Transformation/De-identification node.
      this.toast.show('Updated', `${e.chainLabel ?? t.name} configuration updated.`);
      const edited = this.store.byId(e.editNodeId);
      if (edited && e.transformId.startsWith('dest-')) this.syncChainNodes(edited);
      return;
    }

    // Chain steps (Mapping / Transformation / De-identification) don't hang off whatever was clicked —
    // they always live BETWEEN the source and the destination, in canonical order, so which node's `+`
    // was used only identifies the pipeline, not the position. See insertChainStep.
    if (CHAIN_STEP_IDS.includes(e.transformId)) {
      const destination = this.appSvc.destinationFor(e.attachNode, this.store.nodes(), this.store.edges());
      if (!destination) {
        this.toast.error('No destination', 'Add a destination before adding this step.');
        return;
      }
      this.insertChainStep(destination, e.transformId, e.config);
      this.toast.show('Step added', `${t.name} added.`);
      return;
    }

    const attachNode = e.attachNode;

    const siblings = this.store.outboundEdges(attachNode.id).length;
    const node: TransformNode = {
      id:          this.store.nextTransformId(),
      kind:        'transform',
      transformId: e.transformId,
      sourceName:  this._nodeDisplayName(this.store.rootSourceOf(attachNode) ?? attachNode),
      statusAtAdd: e.status,
      x:           attachNode.x + 300,
      y:           attachNode.y + siblings * 170,
      fields:      { '__name': t.name, ...(e.config ?? {}) },
    };
    this.store.addNode(node);
    this.store.addEdge({ id: this.store.nextEdgeId(), from: attachNode.id, to: node.id });
    this.toast.show(
      'Step added',
      e.status === 'caveat' ? `${t.name} added with caveat.` : `${t.name} added.`,
    );

    if (e.transformId.startsWith('dest-')) this.syncChainNodes(node);
  }

  /**
   * Renders whatever the destination wizard actually configured as its own canvas nodes — the
   * "decomposition" half of V2's design. Configuring field mappings, transformation rules or a
   * de-identification policy inside the wizard is what MAKES those steps part of this pipeline, so the
   * canvas shows them as nodes without the user having to also add each one through the `+` picker
   * (which stays available for adding a step the wizard hasn't configured yet).
   *
   * Idempotent: a step already present in the chain is never duplicated, so re-saving a destination — or
   * saving it repeatedly while adding rules — converges on exactly one node per configured step, in the
   * fixed order Destination → Mapping → Transformation → De-identification.
   */
  private syncChainNodes(destination: CanvasNode): void {
    const fields = destination.fields ?? {};

    let hasMappings = false;
    try {
      const rows = JSON.parse(fields['dest_mappings'] ?? '[]') as unknown[];
      hasMappings = Array.isArray(rows) && rows.length > 0;
    } catch { hasMappings = false; }

    // Mapping only applies to SQL-family destinations (see SQL_FAMILY_DESTINATION_TYPES) — the same gate
    // the picker uses, so the two can't disagree about whether a Mapping node belongs here.
    const destinationType = TRANSFORMS.find(x => x.id === (destination as TransformNode).transformId)?.destinationType;
    const mappingApplies = hasMappings
      && !!destinationType
      && SQL_FAMILY_DESTINATION_TYPES.has(destinationType);

    const hasDeIdentification = !!fields['deIdentificationProfileId'];

    if (mappingApplies) this.ensureChainNode(destination, 'field-mapping');

    // Transformation rules live server-side per destination type, not in the node's own config, so this is
    // the one signal that has to be asked for rather than read locally.
    if (destinationType) {
      this.transformationRules.list({ destinationType }).subscribe({
        next: rules => {
          if (rules.length > 0) this.ensureChainNode(destination, 'transformation');
          if (hasDeIdentification) this.ensureChainNode(destination, 'deidentification');
        },
        // A rules lookup failure shouldn't block the de-identification node the config alone already proves.
        error: () => { if (hasDeIdentification) this.ensureChainNode(destination, 'deidentification'); },
      });
    } else if (hasDeIdentification) {
      this.ensureChainNode(destination, 'deidentification');
    }
  }

  /** Adds `transformId` to `destination`'s chain if it isn't already there (see insertChainStep). */
  private ensureChainNode(destination: CanvasNode, transformId: string): void {
    if (this.chainStepsBefore(destination).some(n => (n as TransformNode).transformId === transformId)) return;
    this.insertChainStep(destination, transformId);
  }

  /** The chain nodes currently sitting between the source and `destination`, in canvas order. */
  private chainStepsBefore(destination: CanvasNode): CanvasNode[] {
    const chain: CanvasNode[] = [];
    let cursor: CanvasNode | undefined = destination;
    let guard = 0;
    while (cursor && guard++ < 20) {
      const inbound: { id: string; from: string; to: string } | undefined = this.store.inboundEdges(cursor.id)[0];
      const predecessor: CanvasNode | undefined = inbound ? this.store.byId(inbound.from) : undefined;
      if (!predecessor || !this.appSvc.isChainStep(predecessor)) break;
      chain.unshift(predecessor);
      cursor = predecessor;
    }
    return chain;
  }

  /**
   * Inserts a chain step into `destination`'s pipeline, BETWEEN the source and that destination — V2's
   * canvas order is Source → Mapping → Transformation → De-identification → Destination.
   *
   * Position is decided by canonical order (Mapping, then Transformation, then De-identification), not by
   * insertion sequence, so adding De-identification before Transformation still ends up rendering as
   * Transformation → De-identification. The whole segment's edges and x/y are rebuilt each time, which
   * keeps the canvas laid out left-to-right in that same order however the steps were added.
   */
  private insertChainStep(destination: CanvasNode, transformId: string, config?: Record<string, string>): void {
    const t = TRANSFORMS.find(x => x.id === transformId);
    if (!t) return;

    const existing = this.chainStepsBefore(destination);
    const head = this.store.inboundEdges(existing.length ? existing[0].id : destination.id)[0];
    const upstream = head ? this.store.byId(head.from) : undefined;

    const node: TransformNode = {
      id:          this.store.nextTransformId(),
      kind:        'transform',
      transformId,
      sourceName:  this._nodeDisplayName(this.store.rootSourceOf(destination) ?? destination),
      statusAtAdd: 'show',
      x:           destination.x,
      y:           destination.y,
      fields:      { '__name': t.name, ...(config ?? {}) },
    };
    this.store.addNode(node);

    // Canonical order for the whole segment, including the newcomer.
    const ordered = [...existing, node as CanvasNode].sort((a, b) =>
      CHAIN_STEP_IDS.indexOf((a as TransformNode).transformId) - CHAIN_STEP_IDS.indexOf((b as TransformNode).transformId));

    // Rewire: drop every edge inside the segment, then relink upstream → ordered… → destination.
    [...existing, node].forEach(n => {
      this.store.inboundEdges(n.id).forEach(edge => this.store.removeEdge(edge.id));
      this.store.outboundEdges(n.id).forEach(edge => this.store.removeEdge(edge.id));
    });
    this.store.inboundEdges(destination.id).forEach(edge => this.store.removeEdge(edge.id));

    const sequence = [...(upstream ? [upstream] : []), ...ordered, destination];
    for (let i = 0; i < sequence.length - 1; i++) {
      this.store.addEdge({ id: this.store.nextEdgeId(), from: sequence[i].id, to: sequence[i + 1].id });
    }

    // Lay the segment out left-to-right from whatever feeds it, pushing the destination to the end.
    const baseX = upstream ? upstream.x : destination.x - 300 * (ordered.length + 1);
    const baseY = upstream ? upstream.y : destination.y;
    ordered.forEach((n, i) => this.store.moveNode(n.id, baseX + 300 * (i + 1), baseY));
    this.store.moveNode(destination.id, baseX + 300 * (ordered.length + 1), baseY);
  }

  // V2 attaches every node exactly where the user clicked `+`, giving the authored straight chain
  // Source → Destination → [Mapping →] Transformation → De-identification. V1's
  // resolveDestinationAttachPoint()/insertMappingNode()/cloneMappingNode() trio lived here and forced a
  // Mapping node in UPSTREAM of each destination, because V1's canvas mirrored the backend's execution
  // topology (Mapping feeds Destination). V2 authors Mapping explicitly AFTER the destination instead, and
  // WorkflowGraphMapperServiceV2.toBackendOrder() lifts the whole chain back in front of the destination at
  // save time — so inserting anything here would both duplicate that Mapping node and break the chain walk
  // that resolves a chain node's owning destination.

  onMergeSelected(e: MergeEvent): void {
    if (!this.canMutate()) return;
    const opt = e.opt;
    let members: CanvasNode[];

    if (opt.source) {
      members = (opt.sourceIds ?? []).map(id => this.store.byId(id)).filter((n): n is CanvasNode => !!n);
    } else {
      const parent = opt.parentId ? this.store.byId(opt.parentId) : undefined;
      if (!parent) return;
      members = this.store.directTransformChildren(parent.id)
        .filter(k => k.kind === 'transform' && this.appSvc.groupOf((k as TransformNode).transformId) === opt.group);
    }
    if (!members.length) return;

    const avgY = members.reduce((s, k) => s + k.y, 0) / members.length;
    const maxX = Math.max(...members.map(k => k.x));
    const merge: MergeNode = {
      id:     this.store.nextMergeId(),
      kind:   'merge',
      group:  opt.group,
      x:      maxX + 300,
      y:      avgY,
      fields: { '__name': 'Merge · ' + this.appSvc.groupLabel(opt.group) },
    };
    this.store.addNode(merge);
    members.forEach(k => {
      if (!this.store.hasEdge(k.id, merge.id))
        this.store.addEdge({ id: this.store.nextEdgeId(), from: k.id, to: merge.id });
    });
    this.toast.show('Merged', `${members.length} "${this.appSvc.groupLabel(opt.group)}" members merged.`);
  }

  // ── wizard (node-circle click on source or transform node) ──────────────────
  // canvas.component's openWizard event always emits a real nodeId (see onNodeConfigure),
  // so the no-nodeId case (formerly opening the legacy epic-source-wizard directly) is unreachable.
  onOpenWizard(nodeId: string): void {
    const node = this.store.byId(nodeId);
    if (node?.kind === 'transform') {
      const tId = (node as TransformNode).transformId;
      // Destinations open their own wizard; V2's chain steps (Mapping / Transformation /
      // De-identification) open that same wizard against their upstream destination, landing on their own
      // tab of its Mapping list — see NodeLibraryDialogComponent's CHAIN_TABS handling. Without the chain
      // ids here, clicking one of those nodes fell through to the "nothing to configure" toast below.
      const isChainStep = tId === 'field-mapping' || tId === 'transformation' || tId === 'deidentification';
      if (tId.startsWith('dest-') || isChainStep) {
        // Edit destination node — open library in transform mode with parent as origin.
        const parent = this.store.parentOf(nodeId);
        this.editingNodeId.set(nodeId);
        this.libraryMode.set('transform');
        this.libraryOriginId.set(parent?.id ?? null);
        this.libraryOpen.set(true);
      } else {
        this.toast.show('Nothing to configure', "This module type doesn't have a configuration screen yet.");
      }
      return;
    }
    // Edit Epic source node → open Node Library with Epic form pre-populated.
    this.editingNodeId.set(nodeId);
    this.libraryMode.set('source');
    this.libraryOriginId.set(null);
    this.libraryOpen.set(true);
  }

  onLibraryClosed(): void {
    this.libraryOpen.set(false);
    this.editingNodeId.set(null);
  }

  // ── checkpoint (Phase 1) ────────────────────────────────────────────────────
  /** The node's checkpointUrlEnabled flag toggles instantly on canvas, but the URL only exists once the backend has
   * persisted it — so this always requires a saved workflow, and re-saving after toggling before copying. */
  onCopyCheckpointUrl(nodeId: string): void {
    const workflowId = this.currentWorkflowId();
    if (!workflowId) {
      this.toast.warning('Save first', 'Save the workflow before copying a checkpoint URL — the node needs a saved id.');
      return;
    }

    this.workflowApi.checkpointUrl(workflowId, nodeId).subscribe({
      next: ({ checkpointUrl }) => {
        navigator.clipboard?.writeText(checkpointUrl).then(
          () => this.toast.success('Copied', 'Checkpoint URL copied to clipboard.'),
          () => this.toast.error('Copy failed', checkpointUrl),
        );
      },
      error: err => {
        const msg = err?.error?.error_description ?? err?.error?.error ?? 'Could not generate a checkpoint URL.';
        this.toast.error('Checkpoint URL failed', typeof msg === 'string' ? msg : 'Save the workflow again and retry.');
      },
    });
  }

  // ── private helpers ────────────────────────────────────────────────────────
  private saveWorkflow(name: string, workflowId?: string | null, activate = false): void {
    if (this.workflowApi.catalog().length === 0) {
      this.workflowStatus.set('Catalog is not loaded yet.');
      return;
    }

    const request = this.graphMapper.toRequest(name, this.buildTrigger());
    this.workflowBusy.set(true);
    this.workflowStatus.set('Validating workflow...');
    this.workflowApi.validate(request).subscribe({
      next: validation => {
        if (!validation.isValid) {
          this.workflowStatus.set(validation.errors[0] ?? 'Workflow validation failed.');
          this.workflowBusy.set(false);
          return;
        }

        this.workflowStatus.set('Saving workflow...');
        this.workflowApi.save(request, workflowId).subscribe({
          next: saved => {
            this.currentWorkflowId.set(saved.id);
            this.workflowIdInput.set(saved.id);
            this.workflowName.set(saved.name);

            if (!activate) {
              this.workflowStatus.set(`Saved ${saved.name}.`);
              this.toast.success('Workflow saved', `"${saved.name}" was saved.`);
              this.workflowBusy.set(false);
              this.finishSave();
              return;
            }

            this.workflowApi.activate(saved.id).subscribe({
              next: active => {
                this.workflowStatus.set(`Saved and activated ${active.name}.`);
                this.toast.success('Workflow saved', `"${active.name}" was saved and activated.`);
                this.workflowBusy.set(false);
                this.finishSave();
              },
              error: () => {
                this.workflowStatus.set('Saved workflow, but activation failed.');
                this.toast.warning('Activation failed', `"${saved.name}" was saved but could not be activated.`);
                this.workflowBusy.set(false);
              },
            });
          },
          error: () => {
            this.workflowStatus.set('Workflow save failed.');
            this.workflowBusy.set(false);
          },
        });
      },
      error: () => {
        this.workflowStatus.set('Workflow validation request failed.');
        this.workflowBusy.set(false);
      },
    });
  }

  // ── HasUnsavedChanges (unsaved-changes.guard.ts) ────────────────────────────
  hasUnsavedChanges(): boolean {
    return this.store.dirty();
  }

  isSaveInProgress(): boolean {
    return this.workflowBusy();
  }

  // ── "Leave this page?" inline confirm ───────────────────────────────────────
  // Rendered directly in this component's own template (see workflow-builder-v2.component.html/.scss)
  // instead of the shared MatDialog-based ConfirmDialogComponent — same markup pattern as
  // destination-wizard's "Exit mapping" confirm (pendingExitConfirm/dw-confirm-*), which is the
  // design reference this needs to match pixel-for-pixel. A route-level CanDeactivate guard still
  // runs while this component is mounted, so an inline overlay works exactly like a modal here.
  readonly pendingLeaveConfirm = signal(false);
  private leaveConfirmResult: Subject<boolean> | null = null;

  confirmLeaveDialog(): Observable<boolean> {
    this.leaveConfirmResult = new Subject<boolean>();
    this.pendingLeaveConfirm.set(true);
    return this.leaveConfirmResult.asObservable();
  }

  cancelLeavePage(): void {
    this.pendingLeaveConfirm.set(false);
    this.leaveConfirmResult?.next(false);
    this.leaveConfirmResult?.complete();
    this.leaveConfirmResult = null;
  }

  onLeaveBackdropClick(e: MouseEvent): void {
    if (e.target === e.currentTarget) this.cancelLeavePage();
  }

  confirmLeavePage(): void {
    this.pendingLeaveConfirm.set(false);
    this.leaveConfirmResult?.next(true);
    this.leaveConfirmResult?.complete();
    this.leaveConfirmResult = null;
  }

  /** Post-save wrap-up — always returns to the Workflows list, where the save (new or updated) is now
   *  visible, instead of leaving you staring at a blanked-out or unchanged canvas. markSaved() first,
   *  or the unsaved-changes guard (which only ever saw the canvas reset itself clean via reset() before)
   *  intercepts this very navigation and asks "Leave this page?" right after a successful save. */
  private finishSave(): void {
    this.store.markSaved();
    this.router.navigate(['/workflows']);
  }

  // Writes each node's real, server-created id back onto its own fields instead of wiping the canvas —
  // the wizards read these fields (findLaunchSourceId(), destination-wizard's resolvedDestinationId) so
  // the Mapping JSON gets non-null sourceConnectionId/destinationId right after Save, no reload needed.
  // Also stamps mappingProfileId so the next Save updates these records in place rather than duplicating
  // them (workflow-build-assembler.service.ts reads these same three field names as each spec's existingId).
  private stampBuildResultIds(result: WorkflowBuildResult): void {
    const stamp = (ids: Record<string, string>, field: string) => {
      for (const [nodeId, id] of Object.entries(ids)) {
        const node = this.store.byId(nodeId);
        if (!node) continue;
        this.store.updateNode(nodeId, { fields: { ...node.fields, [field]: id } });
      }
    };
    stamp(result.sourceConnectionIds, 'sourceConnectionId');
    stamp(result.destinationIds, 'destinationId');
    stamp(result.mappingProfileIds, 'mappingProfileId');
  }

  /** Blanks the canvas and workflow identity — used when explicitly starting a new workflow (see
   *  ngOnInit/onReset), not after a save (see finishSave). */
  private resetCanvasAndWorkflowState(): void {
    this.store.reset();
    this.currentWorkflowId.set(null);
    this.workflowName.set('');
    // Otherwise the freshly-blanked name field reads as invalid immediately — nameTouched stays true from
    // whatever earlier interaction/failed-save-attempt set it, and nameInvalid() only checks
    // nameTouched() && !workflowName().trim(), which is now true again on a field nobody has touched yet.
    this.nameTouched.set(false);
    this.workflowIdInput.set('');
    this.triggerType.set('Manual');
    this.cronExpression.set('0 0 * * *');
    this.pollMinutes.set(15);
  }

  private addStubSource(s: Source): void {
    const count = this.store.nodes().filter(n => !n.kind).length;
    const node: SourceNode = {
      id:             this.store.nextNodeId(),
      kind:           undefined,
      x:              360 + count * 70,
      y:              300 + count * 60,
      connected:      true,
      abbr:           s.abbr,
      color:          s.color,
      connectorLabel: s.name,
      vendorId:       s.id,
      fields: {
        '__name':         s.name,
        'App context':    s.context,
        'Ingestion mode': 'search',
        'Connector':      s.name,
      },
    };
    this.store.addNode(node);
    this.toast.show('Source added', `${s.name} added to the canvas.`);
  }

  private _nodeDisplayName(n: CanvasNode): string {
    return this.appSvc.nodeDisplayName(n);
  }
}
