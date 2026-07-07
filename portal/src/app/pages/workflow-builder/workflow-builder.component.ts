import { Component, OnInit, inject, signal } from '@angular/core';
import { PipelineStore } from '../../services/pipeline.store';
import { WizardService } from '../../services/wizard.service';
import { ToastService } from '../../services/toast.service';
import { ApplicabilityService } from '../../services/applicability.service';
import { WorkflowApiService } from '../../services/workflow-api.service';
import { WorkflowGraphMapperService } from '../../services/workflow-graph-mapper.service';
import { SOURCES } from '../../data/sources.data';
import { TRANSFORMS } from '../../data/transforms.data';
import { Source } from '../../models/source.model';
import { CanvasNode, SourceNode, TransformNode, MergeNode } from '../../models/node.model';

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
  protected readonly workflowName = signal('Pipeline Builder Workflow');
  protected readonly workflowIdInput = signal('');
  protected readonly workflowBusy = signal(false);
  protected readonly workflowStatus = signal('Catalog loading...');

  ngOnInit(): void {
    this.workflowApi.loadCatalog().subscribe({
      next: items => this.workflowStatus.set(`Catalog loaded (${items.length} nodes).`),
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

  onWorkflowIdInput(value: string): void {
    this.workflowIdInput.set(value.trim());
  }

  onSaveWorkflow(): void {
    this.saveWorkflow(this.workflowName().trim() || 'Pipeline Builder Workflow', this.currentWorkflowId());
  }

  onSaveLaunchWorkflow(): void {
    const sourceId = this.graphMapper.findLaunchSourceId();
    if (!sourceId) {
      this.workflowStatus.set('Add a source connection id on the source node before saving for launch.');
      this.toast.show('Launch binding needs a source id', 'Source node is missing Source connection id.');
      return;
    }

    this.saveWorkflow(this.graphMapper.launchWorkflowName(sourceId), this.currentWorkflowId(), true);
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

  onRunWorkflow(): void {
    const id = this.currentWorkflowId();
    if (!id) {
      this.workflowStatus.set('Save or load a workflow before running.');
      return;
    }

    this.workflowBusy.set(true);
    this.workflowStatus.set('Running workflow...');
    this.workflowApi.run(id).subscribe({
      next: result => {
        const run = result.workflowRun ?? result.run;
        this.workflowStatus.set(run?.id ? `Run started: ${run.id}` : 'Workflow run completed.');
        this.workflowBusy.set(false);
      },
      error: () => {
        this.workflowStatus.set('Workflow run failed.');
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

    // Editing an existing destination node's configuration (dest wizard edit flow).
    if (e.editNodeId) {
      this.store.updateNode(e.editNodeId, {
        fields: { '__name': t.name, ...(e.config ?? {}) },
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

  // ── private helpers ────────────────────────────────────────────────────────
  private saveWorkflow(name: string, workflowId?: string | null, activate = false): void {
    if (this.workflowApi.catalog().length === 0) {
      this.workflowStatus.set('Catalog is not loaded yet.');
      return;
    }

    const request = this.graphMapper.toRequest(name);
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
              this.workflowBusy.set(false);
              return;
            }

            this.workflowApi.activate(saved.id).subscribe({
              next: active => {
                this.workflowStatus.set(`Saved and activated ${active.name}.`);
                this.workflowBusy.set(false);
              },
              error: () => {
                this.workflowStatus.set('Saved workflow, but activation failed.');
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
