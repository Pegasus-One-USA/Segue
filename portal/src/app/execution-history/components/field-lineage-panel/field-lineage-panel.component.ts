import { Component, OnInit, inject, input, signal, viewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatMenuModule, MatMenuTrigger } from '@angular/material/menu';
import { ExecutionHistoryApiService } from '../../services/execution-history-api.service';
import {
  FieldLineageChain,
  LineageSummary,
  PagedResult,
  ResourceTypeSummary,
} from '../../models/execution-history.model';
import {
  nodeAbbr,
  nodeAccentVar,
} from '../../../components/node-library/destination-wizard/field-mapping/transform-node-classifier';
import { TransformNodeType } from '../../../components/node-library/destination-wizard/field-mapping/transformation-rules.service';

type GroupByMode = 'field' | 'patient' | 'node';
type DetailTab = 'flow' | 'details' | 'nodeInfo';

@Component({
  selector: 'app-field-lineage-panel',
  standalone: true,
  imports: [CommonModule, MatIconModule, MatPaginatorModule, MatProgressBarModule, MatMenuModule],
  templateUrl: './field-lineage-panel.component.html',
  styleUrls: ['./field-lineage-panel.component.scss'],
})
export class FieldLineagePanelComponent implements OnInit {
  readonly runId = input.required<string>();

  private readonly api = inject(ExecutionHistoryApiService);

  /** The #resourceMenuTriggerBtn="matMenuTrigger" reference in the template — closed explicitly from
   *  selectField() below once a field is picked, since the tree's rows are plain divs (not mat-menu-item),
   *  so the tree itself can toggle a resource type open/closed without MatMenu treating that as "close". */
  private readonly resourceMenuTrigger = viewChild<MatMenuTrigger>('resourceMenuTriggerBtn');

  readonly summary = signal<LineageSummary | null>(null);
  readonly resourceTree = signal<ResourceTypeSummary[]>([]);
  readonly expandedResourceTypes = signal<Set<string>>(new Set());

  readonly groupBy = signal<GroupByMode>('field');
  readonly searchText = signal('');
  readonly selectedResourceType = signal<string | null>(null);
  readonly selectedField = signal<string | null>(null);

  readonly chains = signal<PagedResult<FieldLineageChain>>({ items: [], totalCount: 0, page: 1, pageSize: 25 });
  readonly loading = signal(false);
  /** True when the last loadChains() request failed at the HTTP level (e.g. the backend threw decrypting one
   *  row's PHI value) rather than genuinely returning zero matches — previously indistinguishable, since the
   *  error handler reset chains() to an empty page either way. The empty-state message below reads this to
   *  show "couldn't load" instead of silently implying "nothing here" for what's actually a real failure. */
  readonly loadError = signal(false);
  readonly pageIndex = signal(0);
  readonly pageSize = signal(10);

  readonly selectedChain = signal<FieldLineageChain | null>(null);
  readonly detailTab = signal<DetailTab>('flow');

  ngOnInit(): void {
    this.api.lineageSummary(this.runId()).subscribe({
      next: result => this.summary.set(result),
      error: () => this.summary.set(null),
    });
    this.api.lineageResourceTree(this.runId()).subscribe({
      next: tree => this.resourceTree.set(tree),
      error: () => this.resourceTree.set([]),
    });
    this.loadChains();
  }

  setGroupBy(mode: GroupByMode): void {
    this.groupBy.set(mode);
    this.selectedResourceType.set(null);
    this.selectedField.set(null);
    this.searchText.set('');
    this.pageIndex.set(0);
    this.loadChains();
  }

  onSearchChange(value: string): void {
    this.searchText.set(value);
    this.selectedResourceType.set(null);
    this.selectedField.set(null);
    this.pageIndex.set(0);
    this.loadChains();
  }

  toggleResourceType(resourceType: string): void {
    const next = new Set(this.expandedResourceTypes());
    if (next.has(resourceType)) {
      next.delete(resourceType);
    } else {
      next.add(resourceType);
    }
    this.expandedResourceTypes.set(next);
  }

  isResourceTypeExpanded(resourceType: string): boolean {
    return this.expandedResourceTypes().has(resourceType);
  }

  selectField(resourceType: string, destinationField: string): void {
    this.selectedResourceType.set(resourceType);
    this.selectedField.set(destinationField);
    this.searchText.set('');
    this.pageIndex.set(0);
    this.loadChains();
    this.resourceMenuTrigger()?.closeMenu();
  }

  clearFieldSelection(): void {
    this.selectedResourceType.set(null);
    this.selectedField.set(null);
    this.pageIndex.set(0);
    this.loadChains();
  }

  loadChains(): void {
    this.loading.set(true);
    this.loadError.set(false);
    const filter = {
      resourceType: this.selectedResourceType() ?? undefined,
      destinationField: this.groupBy() === 'field' ? (this.selectedField() ?? undefined) : undefined,
      resourceId: this.groupBy() === 'patient' ? (this.searchText() || undefined) : undefined,
      nodeType: this.groupBy() === 'node' ? (this.searchText() || undefined) : undefined,
      search: this.groupBy() === 'field' && !this.selectedField() ? (this.searchText() || undefined) : undefined,
    };

    this.api.fieldLineage(this.runId(), this.pageIndex() + 1, this.pageSize(), filter).subscribe({
      next: result => {
        this.chains.set(result);
        this.loading.set(false);
      },
      error: () => {
        this.chains.set({ items: [], totalCount: 0, page: 1, pageSize: this.pageSize() });
        this.loadError.set(true);
        this.loading.set(false);
      },
    });
  }

  onPageChange(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.loadChains();
  }

  openDetail(chain: FieldLineageChain): void {
    this.selectedChain.set(chain);
    this.detailTab.set('flow');
  }

  closeDetail(): void {
    this.selectedChain.set(null);
  }

  setDetailTab(tab: DetailTab): void {
    this.detailTab.set(tab);
  }

  chainSucceeded(chain: FieldLineageChain): boolean {
    return chain.hops.every(h => h.success);
  }

  firstSourceValue(chain: FieldLineageChain): string {
    return this.formatValue(chain.hops[0]?.sourceValueJson ?? null);
  }

  lastDestinationValue(chain: FieldLineageChain): string {
    const last = chain.hops[chain.hops.length - 1];
    return this.formatValue(last?.destinationValueJson ?? null);
  }

  formatValue(json: string | null): string {
    if (json === null) return '—';
    try {
      return JSON.stringify(JSON.parse(json));
    } catch {
      return json;
    }
  }

  formatConfig(configJson: string): string {
    try {
      const parsed = JSON.parse(configJson) as Record<string, string>;
      const entries = Object.entries(parsed);
      return entries.length === 0 ? '—' : entries.map(([k, v]) => `${k}: ${v}`).join(', ');
    } catch {
      return configJson;
    }
  }

  /** hop.nodeType is always one of the same 20 transform-node values the mapping wizard's Rules dialog
   *  works with (see transform-node-classifier.ts) — reusing its rank accent/glyph here, instead of a
   *  flat one-color "Transformation Node" treatment, ties this view back to the same node taxonomy a
   *  user already sees when they build the mapping. */
  nodeAccent(nodeType: string): string {
    return nodeAccentVar(nodeType as TransformNodeType);
  }

  nodeGlyph(nodeType: string): string {
    return nodeAbbr(nodeType as TransformNodeType);
  }

  /** Cycles the same --fm-rank-N palette the field-mapping wizard's own source tree uses for its resource
   *  roots (see field-mapping-source-tree.component.ts's groupColorVar()) — so "Patient"/"Observation"/…
   *  reads with the same per-resource-type identity here as it does over in the mapping canvas. */
  resourceTypeAccent(index: number): string {
    return `var(--fm-rank-${(index % 11) + 1})`;
  }

  /** The active-filter chip's accent — the same color its resource type has in the tree popover, so the
   *  chip visibly reads as "this came from the Patient branch you expanded," not just a generic teal tag. */
  selectedResourceTypeAccent(): string {
    const index = this.resourceTree().findIndex(rt => rt.resourceType === this.selectedResourceType());
    return this.resourceTypeAccent(index < 0 ? 0 : index);
  }
}
