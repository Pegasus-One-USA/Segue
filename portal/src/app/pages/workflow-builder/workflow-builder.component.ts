import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { PipelineStore } from '../../services/pipeline.store';
import { WizardService } from '../../services/wizard.service';
import { ToastService } from '../../services/toast.service';
import { ApplicabilityService } from '../../services/applicability.service';
import { SOURCES } from '../../data/sources.data';
import { TRANSFORMS } from '../../data/transforms.data';
import { Source } from '../../models/source.model';
import { CanvasNode, SourceNode, TransformNode, MergeNode } from '../../models/node.model';
import { MergeNodeOption } from '../../models/wizard-state.model';

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
    FormsModule,
    CanvasComponent,
    EpicSourceWizardComponent,
    PayloadPreviewComponent,
    ToastComponent,
    NodeLibraryDialogComponent,
  ],
  templateUrl: './workflow-builder.component.html',
  styleUrl: './workflow-builder.component.scss',
})
export class WorkflowBuilderComponent {
  private readonly store  = inject(PipelineStore);
  private readonly wiz    = inject(WizardService);
  private readonly toast  = inject(ToastService);
  private readonly appSvc = inject(ApplicabilityService);

  // ── page state ─────────────────────────────────────────────────────────────
  protected readonly scenarioName = signal('Grouped-node pipeline (Normalize group + merge)');

  // ── node library dialog (unified — replaces source + transform pickers) ────
  protected readonly libraryOpen      = signal(false);
  protected readonly libraryMode      = signal<LibraryMode>('source');
  protected readonly libraryOriginId  = signal<string | null>(null);

  // ── other modals ───────────────────────────────────────────────────────────
  protected readonly payloadOpen = signal(false);
  protected readonly wizardOpen  = this.wiz.isOpen;

  // ── topbar ─────────────────────────────────────────────────────────────────
  onPreview(): void { this.payloadOpen.set(true); }
  onReset(): void {
    this.store.reset();
    this.toast.show('Canvas reset', 'All nodes removed.');
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
    const siblings = this.store.outboundEdges(e.attachNode.id).length;
    const node: TransformNode = {
      id:          this.store.nextTransformId(),
      kind:        'transform',
      transformId: e.transformId,
      sourceName:  this._nodeDisplayName(this.store.rootSourceOf(e.attachNode) ?? e.attachNode),
      statusAtAdd: e.status,
      x:           e.attachNode.x + 300,
      y:           e.attachNode.y + siblings * 170,
      fields:      { '__name': t.name },
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

  // ── wizard (node-circle click on source node) ──────────────────────────────
  onOpenWizard(nodeId?: string): void { this.wiz.open(nodeId); }

  // ── private helpers ────────────────────────────────────────────────────────
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
