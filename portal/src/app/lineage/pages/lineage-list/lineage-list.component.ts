import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { Subject, debounceTime, distinctUntilChanged } from 'rxjs';
import { LineageApiService } from '../../services/lineage-api.service';
import { FieldLineageEntry, LineageChain, LineageEntry, PagedResult } from '../../models/lineage.model';

@Component({
  selector: 'app-lineage-list',
  standalone: true,
  imports: [
    CommonModule,
    DatePipe,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatPaginatorModule,
  ],
  templateUrl: './lineage-list.component.html',
  styleUrls: ['./lineage-list.component.scss'],
})
export class LineageListComponent implements OnInit, OnDestroy {
  private readonly api = inject(LineageApiService);
  private readonly resourceType$ = new Subject<string>();

  readonly resourceTypeFilter = signal('');
  readonly actionFilter = signal('');
  readonly statusFilter = signal('');
  readonly pageIndex = signal(0);
  readonly pageSize = signal(10);
  readonly loading = signal(false);
  readonly result = signal<PagedResult<LineageEntry>>({ items: [], totalCount: 0, page: 1, pageSize: 10 });

  readonly selectedEntry = signal<LineageEntry | null>(null);
  readonly chain = signal<LineageChain | null>(null);
  readonly chainLoading = signal(false);
  readonly fields = signal<FieldLineageEntry[]>([]);
  readonly fieldsLoading = signal(false);

  readonly displayedCols = ['index', 'resourceType', 'action', 'status', 'occurredOnUtc'];

  ngOnInit(): void {
    this.resourceType$.pipe(debounceTime(300), distinctUntilChanged()).subscribe(value => {
      this.resourceTypeFilter.set(value);
      this.pageIndex.set(0);
      this.load();
    });

    this.load();
  }

  ngOnDestroy(): void {
    this.resourceType$.complete();
  }

  load(): void {
    this.loading.set(true);
    this.api.list({
      resourceType: this.resourceTypeFilter() || undefined,
      action: this.actionFilter() || undefined,
      status: this.statusFilter() || undefined,
      page: this.pageIndex() + 1,
      pageSize: this.pageSize(),
    }).subscribe({
      next: result => {
        this.result.set(result);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  onResourceType(val: string): void { this.resourceType$.next(val); }
  onAction(val: string): void { this.actionFilter.set(val); this.pageIndex.set(0); this.load(); }
  onStatus(val: string): void { this.statusFilter.set(val); this.pageIndex.set(0); this.load(); }

  reset(): void {
    this.resourceTypeFilter.set('');
    this.actionFilter.set('');
    this.statusFilter.set('');
    this.pageIndex.set(0);
    this.load();
  }

  onPageChange(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.load();
  }

  viewChain(entry: LineageEntry): void {
    this.selectedEntry.set(entry);
    this.chain.set(null);
    this.chainLoading.set(true);
    this.fields.set([]);

    this.api.chain({
      pipelineRunId: entry.pipelineRunId,
      resourceType: entry.resourceType,
      sourceResourceId: entry.sourceResourceId,
    }).subscribe({
      next: chain => {
        this.chain.set(chain);
        this.chainLoading.set(false);
      },
      error: () => this.chainLoading.set(false),
    });

    if (!entry.sourceResourceId) {
      return;
    }

    this.fieldsLoading.set(true);
    this.api.fields({
      resourceType: entry.resourceType,
      sourceResourceId: entry.sourceResourceId,
      pipelineRunId: entry.pipelineRunId,
    }).subscribe({
      next: fields => {
        this.fields.set(fields);
        this.fieldsLoading.set(false);
      },
      error: () => this.fieldsLoading.set(false),
    });
  }

  closeChain(): void {
    this.selectedEntry.set(null);
    this.chain.set(null);
    this.fields.set([]);
  }

  readonly showingFrom = () =>
    this.result().totalCount === 0 ? 0 : this.pageIndex() * this.pageSize() + 1;

  readonly showingTo = () =>
    Math.min((this.pageIndex() + 1) * this.pageSize(), this.result().totalCount);

  actionClass(action: string): string {
    return 'action-' + action.toLowerCase().replace(/[^a-z0-9]/g, '');
  }
}
