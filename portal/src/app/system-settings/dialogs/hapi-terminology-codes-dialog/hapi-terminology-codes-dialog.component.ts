import { Component, DestroyRef, OnInit, inject, signal, computed } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { MatDialogModule } from '@angular/material/dialog';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { debounceTime, distinctUntilChanged, Subject } from 'rxjs';
import { HapiTerminologyConfigurationService, TerminologyConcept } from '../../services/hapi-terminology-configuration.service';
import { HapiTerminologyCodeDialogComponent, HapiTerminologyCodeDialogData } from '../hapi-terminology-code-dialog/hapi-terminology-code-dialog.component';
import { ConfirmDialogComponent } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';
import { DialogService, DIALOG_DATA } from '../../../core/services/dialog.service';

export interface HapiTerminologyCodesDialogData {
  systemCode: string;
  displayName: string;
}

/** "View All Codes" screen for one HAPI terminology system — server-side paged/searchable browse of
 * its locally stored codes (TRM_CONCEPT), plus manual add/edit/delete. Opened from the ⋮ menu on
 * HapiTerminologyTableComponent's row for that system. */
@Component({
  selector: 'app-hapi-terminology-codes-dialog',
  standalone: true,
  imports: [
    CommonModule,
    MatDialogModule,
    MatTableModule,
    MatButtonModule,
    MatIconModule,
    MatPaginatorModule,
  ],
  templateUrl: './hapi-terminology-codes-dialog.component.html',
  styleUrls: ['./hapi-terminology-codes-dialog.component.scss'],
})
export class HapiTerminologyCodesDialogComponent implements OnInit {
  private readonly svc       = inject(HapiTerminologyConfigurationService);
  private readonly dialog    = inject(DialogService);
  private readonly toast     = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  readonly data: HapiTerminologyCodesDialogData = inject(DIALOG_DATA) as HapiTerminologyCodesDialogData;

  readonly searchQuery = signal('');
  readonly pageIndex   = signal(0);
  readonly pageSize    = signal(25);
  readonly totalCount  = signal(0);
  readonly loading     = signal(true);
  /** True only while a debounced search request is in flight — drives the small in-field spinner
   *  instead of the screen-blocking global loader. */
  readonly searching   = signal(false);
  readonly concepts    = signal<TerminologyConcept[]>([]);

  readonly displayedCols = [
    'index', 'code', 'display', 'shortDescription', 'longDescription', 'longCommonName', 'isActive', 'actions',
  ];

  private readonly searchChanged = new Subject<string>();

  readonly showingFrom = computed(() =>
    this.totalCount() === 0 ? 0 : this.pageIndex() * this.pageSize() + 1
  );

  readonly showingTo = computed(() =>
    Math.min((this.pageIndex() + 1) * this.pageSize(), this.totalCount())
  );

  ngOnInit(): void {
    this.searchChanged.pipe(
      debounceTime(300),
      distinctUntilChanged(),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe(() => {
      this.pageIndex.set(0);
      this.load(true);
    });

    this.load();
  }

  /** `silent` comes only from the debounced search box — see getCodes. */
  load(silent = false): void {
    this.loading.set(true);
    if (silent) this.searching.set(true);
    this.svc.getCodes(this.data.systemCode, this.searchQuery().trim() || undefined, this.pageIndex() + 1, this.pageSize(), silent)
      .subscribe({
        next: result => {
          this.searching.set(false);
          this.concepts.set(result.items);
          this.totalCount.set(result.totalCount);
          this.loading.set(false);
        },
        error: () => {
          this.searching.set(false);
          this.loading.set(false);
          this.toast.error(`Failed to load ${this.data.displayName} codes.`);
        },
      });
  }

  onSearch(val: string): void {
    this.searchQuery.set(val);
    this.searchChanged.next(val);
  }

  reset(): void {
    this.searchQuery.set('');
    this.pageIndex.set(0);
    this.load();
  }

  onPageChange(e: PageEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.load();
  }

  openAdd(): void {
    this.dialog
      .open<HapiTerminologyCodeDialogComponent, HapiTerminologyCodeDialogData, boolean>(HapiTerminologyCodeDialogComponent, {
        width: '480px',
        disableClose: true,
        data: { systemCode: this.data.systemCode, displayName: this.data.displayName },
      })
      .afterClosed()
      .subscribe(saved => {
        if (saved) {
          this.toast.success('Code added.');
          this.load();
        }
      });
  }

  openEdit(concept: TerminologyConcept): void {
    this.dialog
      .open<HapiTerminologyCodeDialogComponent, HapiTerminologyCodeDialogData, boolean>(HapiTerminologyCodeDialogComponent, {
        width: '480px',
        disableClose: true,
        data: { systemCode: this.data.systemCode, displayName: this.data.displayName, concept },
      })
      .afterClosed()
      .subscribe(saved => {
        if (saved) {
          this.toast.success('Code updated.');
          this.load();
        }
      });
  }

  confirmDelete(concept: TerminologyConcept): void {
    this.dialog
      .open(ConfirmDialogComponent, {
        width: '420px',
        data: {
          title: 'Delete Code',
          message: `Are you sure you want to delete code "${concept.code}"? This cannot be undone.`,
          confirmLabel: 'Delete',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        this.svc.deleteCode(this.data.systemCode, concept.pid).subscribe({
          next: () => {
            this.toast.success(`Code "${concept.code}" deleted.`);
            this.load();
          },
          error: () => this.toast.error(`Could not delete code "${concept.code}".`),
        });
      });
  }
}
