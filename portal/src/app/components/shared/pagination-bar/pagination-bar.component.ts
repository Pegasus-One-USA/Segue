import { Component, computed, input, output } from '@angular/core';

export interface PageChangeEvent {
  pageIndex: number;
  pageSize: number;
}

/** Design-spec §16 pagination footer (Prev/Next + "Showing X–Y of Z" + page-size select) — the same
 *  presentation used on the Workflows list, now shared so every server-paginated table looks the same
 *  instead of falling back to Angular Material's default mat-paginator look. Purely presentational:
 *  the host page still owns pageIndex/pageSize/totalCount and reloads its data on pageChange. */
@Component({
  selector: 'app-pagination-bar',
  standalone: true,
  templateUrl: './pagination-bar.component.html',
  styleUrl: './pagination-bar.component.scss',
})
export class PaginationBarComponent {
  readonly pageIndex = input.required<number>();
  readonly pageSize = input.required<number>();
  readonly totalCount = input.required<number>();
  readonly pageSizeOptions = input<number[]>([10, 20, 50]);

  readonly pageChange = output<PageChangeEvent>();

  readonly totalPages = computed(() => Math.max(1, Math.ceil(this.totalCount() / this.pageSize())));

  readonly rangeStart = computed(() => (this.totalCount() === 0 ? 0 : this.pageIndex() * this.pageSize() + 1));
  readonly rangeEnd = computed(() => Math.min(this.totalCount(), this.rangeStart() + this.pageSize() - 1));

  onPageSizeChange(size: number): void {
    this.pageChange.emit({ pageIndex: 0, pageSize: size });
  }

  prevPage(): void {
    if (this.pageIndex() === 0) return;
    this.pageChange.emit({ pageIndex: this.pageIndex() - 1, pageSize: this.pageSize() });
  }

  nextPage(): void {
    if (this.pageIndex() >= this.totalPages() - 1) return;
    this.pageChange.emit({ pageIndex: this.pageIndex() + 1, pageSize: this.pageSize() });
  }
}
