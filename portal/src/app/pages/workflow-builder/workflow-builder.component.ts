import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { PipelineStore } from '../../services/pipeline.store';
import { WizardService } from '../../services/wizard.service';
import { ToastService } from '../../services/toast.service';
import { ApplicabilityService } from '../../services/applicability.service';
import { WorkflowApiService, WorkflowBuildRequest, WorkflowTriggerRequest } from '../../services/workflow-api.service';
import { WorkflowGraphMapperService } from '../../services/workflow-graph-mapper.service';
import { WorkflowBuildAssemblerService } from '../../services/workflow-build-assembler.service';
import { SOURCES } from '../../data/sources.data';
import { TRANSFORMS } from '../../data/transforms.data';
import { Source } from '../../models/source.model';
import { CanvasNode, SourceNode, TransformNode, MergeNode, isSourceNode } from '../../models/node.model';

import { CanvasComponent } from '../../components/canvas/canvas.component';
import { EpicSourceWizardComponent } from '../../components/epic-source-wizard/epic-source-wizard.component';
import { PayloadPreviewComponent } from '../../components/modals/payload-preview/payload-preview.component';
import { ToastComponent } from '../../components/shared/toast/toast.component';
import {
  NodeLibraryDialogComponent,
  LibraryMode,
  AddTransformEvent,
  MergeEvent,
} from '../../components/node-library/node-library-dialog.component';

@Component({
  selector: 'app-workflow-builder',
  standalone: true,
  imports: [
    CanvasComponent,
    EpicSourceWizardComponent,
    PayloadPreviewComponent,
    ToastComponent,
    NodeLibraryDialogComponent,
  ],
  templateUrl: './workflow-builder.component.html',
  styleUrl: './workflow-builder.component.scss',
})
export class WorkflowBuilderComponent implements OnInit {
  private readonly store  = inject(PipelineStore);
  private readonly wiz    = inject(WizardService);
  private readonly toast  = inject(ToastService);
  private readonly appSvc = inject(ApplicabilityService);
  private readonly workflowApi = inject(WorkflowApiService);
  private readonly graphMapper = inject(WorkflowGraphMapperService);
  private readonly buildAssembler = inject(WorkflowBuildAssemblerService);
  private readonly route = inject(ActivatedRoute);

  // ── page state ─────────────────────────────────────────────────────────────
  protected readonly scenarioName = signal('Grouped-node pipeline (Normalize group + merge)');

  // ── node library dialog (unified — replaces source + transform pickers) ────
  protected readonly libraryOpen      = signal(false);
  protected readonly libraryMode      = signal<LibraryMode>('source');
  protected readonly libraryOriginId  = signal<string | null>(null);
  protected readonly editingNodeId    = signal<string | null>(null);

  // ── other modals ───────────────────────────────────────────────────────────
  protected readonly payloadOpen  = signal(false);
  protected readonly wizardOpen   = this.wiz.isOpen;
  protected readonly confirmReset = signal(false);
  protected readonly currentWorkflowId = signal<string | null>(null);
  protected readonly workflowName = signal('');
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
      .find(fields => !!fields['Retrieval method key'] && !!fields['Run mode']) ?? null);

  protected readonly isWizardDrivenTrigger = computed(() => this.backendRetrievalFields() !== null);

  /** Human-readable summary of the wizard-derived trigger, shown in place of the manual toolbar control. */
  protected readonly wizardTriggerSummary = computed(() => {
    const fields = this.backendRetrievalFields();
    if (!fields) return '';
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
      switch (wizardFields['Run mode']) {
        case 'incremental':
          return { type: 'Poll', intervalMinutes: this.pollFrequencyToMinutes(wizardFields['Schedule / poll frequency']) };
        case 'full':
          return { type: 'Schedule', scheduleExpression: wizardFields['Full refresh schedule (cron)'] || '0 2 * * *' };
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
    if (this.workflowApi.catalog().length === 0) {
      this.workflowStatus.set('Catalog is not loaded yet.');
      return;
    }

    const name = this.workflowName().trim() || 'Untitled workflow';
    const existingId = this.currentWorkflowId();
    const isLaunch = !!this.graphMapper.findLaunchSourceId();

    const request = this.buildAssembler.assemble(name, this.buildTrigger());
    const hasSpecs = (request.sources?.length ?? 0) > 0 || (request.destinations?.length ?? 0) > 0;
    if (hasSpecs) {
      this.buildWorkflow({ ...request, workflowId: existingId ?? undefined });
      return;
    }

    // No wizard-drawn specs → plain design save.
    this.saveWorkflow(name, existingId, isLaunch);
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
        this.workflowBusy.set(false);
        this.resetCanvasAndWorkflowState();
      },
      error: err => {
        const msg = err?.error?.error ?? err?.error ?? err?.message ?? 'Create-on-save failed.';
        this.workflowStatus.set(typeof msg === 'string' ? msg : 'Create-on-save failed.');
        this.toast.show('Create-on-save failed', typeof msg === 'string' ? msg : 'See status for details.');
        this.workflowBusy.set(false);
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
  onOpenSourcePicker(): void {
    this.libraryMode.set('source');
    this.libraryOriginId.set(null);
    this.libraryOpen.set(true);
  }

  onOpenTransformPicker(nodeId: string): void {
    this.libraryMode.set('transform');
    this.libraryOriginId.set(nodeId);
    this.libraryOpen.set(true);
  }

  // ── node library dialog outputs ────────────────────────────────────────────
  onSourceSelected(id: string): void {
    if (id === 'epic') {
      this.wiz.open();
      return;
    }
    const src = SOURCES.find(s => s.id === id);
    if (!src) return;
    this.addStubSource(src);
  }

  onTransformSelected(e: AddTransformEvent): void {
    const t = TRANSFORMS.find(x => x.id === e.transformId);
    if (!t) return;

    // Editing an existing destination node's configuration (dest wizard edit flow). Merge onto the node's existing
    // fields rather than replacing them outright — server-injected machine keys (destinationId, mappingProfileId,
    // secretKeyVaultName/secretName from create-on-save) aren't surfaced as form controls here and must survive.
    if (e.editNodeId) {
      const previousFields = this.store.byId(e.editNodeId)?.fields ?? {};
      this.store.updateNode(e.editNodeId, {
        fields: { ...previousFields, '__name': t.name, ...(e.config ?? {}) },
      });
      this.toast.show('Updated', `${t.name} configuration updated.`);
      return;
    }

    const siblings = this.store.outboundEdges(e.attachNode.id).length;
    const node: TransformNode = {
      id:          this.store.nextTransformId(),
      kind:        'transform',
      transformId: e.transformId,
      sourceName:  this._nodeDisplayName(this.store.rootSourceOf(e.attachNode) ?? e.attachNode),
      statusAtAdd: e.status,
      x:           e.attachNode.x + 300,
      y:           e.attachNode.y + siblings * 170,
      fields:      { '__name': t.name, ...(e.config ?? {}) },
    };
    this.store.addNode(node);
    this.store.addEdge({ id: this.store.nextEdgeId(), from: e.attachNode.id, to: node.id });
    this.toast.show(
      'Step added',
      e.status === 'caveat' ? `${t.name} added with caveat.` : `${t.name} added.`,
    );
  }

  onMergeSelected(e: MergeEvent): void {
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
  onOpenWizard(nodeId?: string): void {
    if (nodeId) {
      const node = this.store.byId(nodeId);
      if (node?.kind === 'transform') {
        const tId = (node as TransformNode).transformId;
        if (tId === 'dest-sqlserver' || tId === 'dest-csv') {
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
    } else {
      this.wiz.open();
    }
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
      this.toast.show('Save first', 'Save the workflow before copying a checkpoint URL — the node needs a saved id.');
      return;
    }

    this.workflowApi.checkpointUrl(workflowId, nodeId).subscribe({
      next: ({ checkpointUrl }) => {
        navigator.clipboard?.writeText(checkpointUrl).then(
          () => this.toast.success('Copied', 'Checkpoint URL copied to clipboard.'),
          () => this.toast.show('Copy failed', checkpointUrl),
        );
      },
      error: err => {
        const msg = err?.error?.error_description ?? err?.error?.error ?? 'Could not generate a checkpoint URL.';
        this.toast.show('Checkpoint URL failed', typeof msg === 'string' ? msg : 'Save the workflow again and retry.');
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
              this.resetCanvasAndWorkflowState();
              return;
            }

            this.workflowApi.activate(saved.id).subscribe({
              next: active => {
                this.workflowStatus.set(`Saved and activated ${active.name}.`);
                this.toast.success('Workflow saved', `"${active.name}" was saved and activated.`);
                this.workflowBusy.set(false);
                this.resetCanvasAndWorkflowState();
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

  /** Blanks the canvas and workflow identity after a successful save, so the builder is ready for the next one. */
  private resetCanvasAndWorkflowState(): void {
    this.store.reset();
    this.currentWorkflowId.set(null);
    this.workflowName.set('');
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
