import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { debounceTime, distinctUntilChanged, Subject } from 'rxjs';
import { IAllowedCorsOriginService } from '../../services/i-allowed-cors-origin.service';
import { AllowedCorsOrigin } from '../../models/allowed-cors-origin.model';
import { AllowedCorsOriginDialogComponent } from '../../dialogs/allowed-cors-origin-dialog/allowed-cors-origin-dialog.component';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';
import { DialogService } from '../../../core/services/dialog.service';

@Component({
  selector: 'app-allowed-cors-origin-list',
  standalone: true,
  imports: [
    CommonModule,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatMenuModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
    MatPaginatorModule,
  ],
  templateUrl: './allowed-cors-origin-list.component.html',
  styleUrls: ['./allowed-cors-origin-list.component.scss'],
})
export class AllowedCorsOriginListComponent implements OnInit {
  private readonly svc    = inject(IAllowedCorsOriginService);
  private readonly customDialog = inject(DialogService);
  private readonly toast  = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  readonly loading = signal(true);
  /** True only while a debounced search-box request is in flight — drives the small in-field
   *  spinner that replaces the screen-blocking global loader for search. */
  readonly searching = signal(false);
  readonly origins  = signal<AllowedCorsOrigin[]>([]);

  /** Free-text search, applied SERVER-side (matches origin URL or label) — the same paged/searchable
   *  contract EHR Endpoints and the terminology code browser use. The CORS policy provider still reads the
   *  unpaged list, so paging here narrows only what the screen shows, never what the API allows. */
  readonly searchQuery = signal('');
  readonly pageIndex  = signal(0);
  readonly pageSize   = signal(10);
  readonly totalCount = signal(0);

  readonly showingFrom = computed(() =>
    this.totalCount() === 0 ? 0 : this.pageIndex() * this.pageSize() + 1
  );

  readonly showingTo = computed(() =>
    Math.min((this.pageIndex() + 1) * this.pageSize(), this.totalCount())
  );

  readonly displayedCols = ['actions', 'originUrl', 'label', 'createdOnUtc', 'createdBy'];

  private readonly searchChanged = new Subject<string>();

  ngOnInit(): void {
    // Debounced so typing does not fire a request per keystroke; resets to page 1 because the
    // current page index is meaningless against a newly filtered result set.
    this.searchChanged.pipe(
      debounceTime(300),
      distinctUntilChanged(),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe(() => {
      this.pageIndex.set(0);
      this.loadOrigins(true);
    });

    this.loadOrigins();
  }

  /** `silent` comes only from the debounced search box: it swaps the app-wide global loader for
   *  the small in-field spinner, so the input being typed into is never blurred or made inert. */
  loadOrigins(silent = false): void {
    this.loading.set(true);
    if (silent) this.searching.set(true);
    this.svc.getPaged({
      search: this.searchQuery().trim() || undefined,
      page: this.pageIndex() + 1,
      pageSize: this.pageSize(),
    }, silent).subscribe({
      next: result => {
        this.searching.set(false);
        this.origins.set(result.items);
        this.totalCount.set(result.totalCount);
        this.loading.set(false);
      },
      error: () => {
        this.searching.set(false);
        this.loading.set(false);
        this.toast.error('Failed to load allowed origins.');
      },
    });
  }

  onSearchChange(val: string): void {
    this.searchQuery.set(val);
    this.searchChanged.next(val);
  }

  reset(): void {
    this.searchQuery.set('');
    this.pageIndex.set(0);
    this.loadOrigins();
  }

  onPageChange(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.loadOrigins();
  }

  openAdd(): void {
    this.customDialog
      .open<AllowedCorsOriginDialogComponent, {}, boolean>(AllowedCorsOriginDialogComponent, {
        width: '480px',
        disableClose: true,
      })
      .afterClosed()
      .subscribe(res => {
        if (res) {
          this.toast.success('Origin added — takes effect immediately, no restart needed.');
          this.loadOrigins();
        }
      });
  }

  openEdit(origin: AllowedCorsOrigin): void {
    this.customDialog
      .open<AllowedCorsOriginDialogComponent, { origin: AllowedCorsOrigin }, boolean>(AllowedCorsOriginDialogComponent, {
        width: '480px',
        disableClose: true,
        data: { origin },
      })
      .afterClosed()
      .subscribe(res => {
        if (res) {
          this.toast.success('Origin updated — takes effect immediately, no restart needed.');
          this.loadOrigins();
        }
      });
  }

  confirmDelete(origin: AllowedCorsOrigin): void {
    this.customDialog
      .open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
        width: '420px',
        data: {
          title: 'Remove Allowed Origin',
          message: `Are you sure you want to remove "${origin.originUrl}"? Browsers at this origin will lose API access immediately.`,
          confirmLabel: 'Remove',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        this.svc.delete(origin.id).subscribe({
          next: () => {
            this.toast.success(`"${origin.originUrl}" removed.`);
            this.loadOrigins();
          },
          error: (err: HttpErrorResponse) => {
            const message = err.error?.title ?? 'Failed to remove origin.';
            this.toast.error(message);
          },
        });
      });
  }
}
