import { Component, OnInit, inject, input, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatIconModule } from '@angular/material/icon';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { ExecutionHistoryApiService } from '../../services/execution-history-api.service';
import {
  FieldLineageChain,
  LineageSummary,
  PagedResult,
  ResourceTypeSummary,
} from '../../models/execution-history.model';

type GroupByMode = 'field' | 'patient' | 'node';
type DetailTab = 'flow' | 'details' | 'nodeInfo';

@Component({
  selector: 'app-field-lineage-panel',
  standalone: true,
  imports: [CommonModule, MatIconModule, MatPaginatorModule],
  templateUrl: './field-lineage-panel.component.html',
  styleUrls: ['./field-lineage-panel.component.scss'],
})
export class FieldLineagePanelComponent implements OnInit {
  readonly runId = input.required<string>();

  private readonly api = inject(ExecutionHistoryApiService);

  readonly summary = signal<LineageSummary | null>(null);
  readonly resourceTree = signal<ResourceTypeSummary[]>([]);
  readonly expandedResourceTypes = signal<Set<string>>(new Set());

  readonly groupBy = signal<GroupByMode>('field');
  readonly searchText = signal('');
  readonly selectedResourceType = signal<string | null>(null);
  readonly selectedField = signal<string | null>(null);

  readonly chains = signal<PagedResult<FieldLineageChain>>({ items: [], totalCount: 0, page: 1, pageSize: 25 });
  readonly loading = signal(false);
  readonly pageIndex = signal(0);
  readonly pageSize = signal(10);

  readonly selectedChain = signal<FieldLineageChain | null>(null);
  readonly detailTab = signal<DetailTab>('flow');

  readonly groupByLabel = computed(() => {
    switch (this.groupBy()) {
      case 'patient': return 'Search by resource ID to see every field touched for that resource.';
      case 'node': return 'Search by node type to see every field that passed through it.';
      default: return 'Pick a field from the resource tree, or search, to see every resource that touched it.';
    }
  });

  ngOnInit(): void {
    this.api.lineageSummary(this.runId()).subscribe(result => this.summary.set(result));
    this.api.lineageResourceTree(this.runId()).subscribe(tree => this.resourceTree.set(tree));
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
  }

  clearFieldSelection(): void {
    this.selectedResourceType.set(null);
    this.selectedField.set(null);
    this.pageIndex.set(0);
    this.loadChains();
  }

  loadChains(): void {
    this.loading.set(true);
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
      error: () => this.loading.set(false),
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
}
