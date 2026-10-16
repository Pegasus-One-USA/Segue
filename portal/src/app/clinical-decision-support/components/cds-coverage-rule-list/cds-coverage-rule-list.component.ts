import { Component, DestroyRef, OnInit, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HttpErrorResponse } from '@angular/common/http';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Subject, Subscription, debounceTime } from 'rxjs';
import { CdsCoverageApiService, cdsSaveFile } from '../../services/cds-coverage-api.service';
import { CdsCoverageFileFormat, CdsCoverageImportResult, CdsCoverageRule, CdsPayer } from '../../models/cds-coverage.models';
import { cdsDocNeededLabel, cdsDocPurposeLabel, cdsInfoNeededLabel, cdsRuleSummary } from '../../models/cds-coverage-labels';
import {
  CdsCoverageRuleDialogComponent,
  CdsCoverageRuleDialogData,
} from '../../dialogs/cds-coverage-rule-dialog/cds-coverage-rule-dialog.component';
import {
  CdsCoverageImportDialogComponent,
  CdsCoverageImportDialogData,
} from '../../dialogs/cds-coverage-import-dialog/cds-coverage-import-dialog.component';
import { DialogService } from '../../../core/services/dialog.service';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { PermissionService } from '../../../auth/services/permission.service';
import { HideWithoutPermissionDirective } from '../../../auth/directives/hide-without-permission.directive';
import { PageChangeEvent, PaginationBarComponent } from '../../../components/shared/pagination-bar/pagination-bar.component';

/**
 * The coverage rules of every payer, a page at a time, filtered by payer, code, text and on / off. Add, edit and
 * delete rules; import a CSV or JSON file (preview first) and export the rules in the same format.
 */
@Component({
  selector: 'app-cds-coverage-rule-list',
  standalone: true,
  imports: [MatIconModule, MatButtonModule, MatMenuModule, MatTooltipModule, HideWithoutPermissionDirective, PaginationBarComponent],
  templateUrl: './cds-coverage-rule-list.component.html',
  styleUrl: './cds-coverage-rule-list.component.scss',
})
export class CdsCoverageRuleListComponent implements OnInit {
  private readonly api         = inject(CdsCoverageApiService);
  private readonly dialog      = inject(DialogService);
  private readonly toast       = inject(ToastService);
  private readonly actionGuard = inject(PermissionActionGuard);
  private readonly permissions = inject(PermissionService);
  private readonly destroyRef  = inject(DestroyRef);

  readonly payers = input.required<CdsPayer[]>();
  /** Fired after rules change (rule counts on the payer list move). */
  readonly changed = output<void>();

  readonly loading    = signal(true);
  readonly rules      = signal<CdsCoverageRule[]>([]);
  readonly totalCount = signal(0);
  readonly pageIndex  = signal(0);
  readonly pageSize   = signal(25);

  readonly payerFilter   = signal('');
  readonly codeFilter    = signal('');
  readonly searchFilter  = signal('');
  readonly enabledFilter = signal<'' | 'true' | 'false'>('');

  readonly summary = cdsRuleSummary;

  readonly hasActiveFilter = computed(() =>
    !!this.payerFilter() || !!this.codeFilter() || !!this.searchFilter() || !!this.enabledFilter());
  readonly hasRowMenu = computed(() =>
    this.permissions.hasPermission('cdshooks.edit') || this.permissions.hasPermission('cdshooks.delete'));

  private readonly typing = new Subject<void>();
  private pending?: Subscription;

  constructor() {
    this.typing.pipe(debounceTime(300), takeUntilDestroyed(this.destroyRef)).subscribe(() => {
      this.pageIndex.set(0);
      this.load();
    });
    this.destroyRef.onDestroy(() => this.pending?.unsubscribe());

    // A payer the list is filtered by was deleted: drop the filter, so the list, Add, Import and Export do not keep
    // using an id that no longer exists.
    effect(() => {
      const payers = this.payers();
      const filter = untracked(this.payerFilter);
      if (filter && !payers.some(payer => payer.id === filter)) {
        untracked(() => this.applyNow(() => this.payerFilter.set('')));
      }
    });
  }

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    // Only the latest filters' answer may land.
    this.pending?.unsubscribe();
    const enabled = this.enabledFilter();
    this.pending = this.api.getRules({
      payerId: this.payerFilter() || undefined,
      code: this.codeFilter().trim() || undefined,
      search: this.searchFilter().trim() || undefined,
      enabled: enabled === '' ? undefined : enabled === 'true',
      page: this.pageIndex() + 1,
      pageSize: this.pageSize(),
    }).subscribe({
      next: page => {
        // The last row of a later page was deleted (or a replace import removed rules): go to the last page that
        // still has rows instead of showing an empty one.
        const lastPage = Math.max(0, Math.ceil(page.totalCount / this.pageSize()) - 1);
        if (page.items.length === 0 && page.totalCount > 0 && this.pageIndex() > lastPage) {
          this.pageIndex.set(lastPage);
          this.load();
          return;
        }
        this.rules.set(page.items);
        this.totalCount.set(page.totalCount);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.toast.error('Could not load the coverage rules.');
      },
    });
  }

  setPayer(value: string): void { this.applyNow(() => this.payerFilter.set(value)); }
  setEnabled(value: string): void { this.applyNow(() => this.enabledFilter.set(value as '' | 'true' | 'false')); }

  setCode(value: string): void {
    this.codeFilter.set(value);
    this.typing.next();
  }

  setSearch(value: string): void {
    this.searchFilter.set(value);
    this.typing.next();
  }

  reset(): void {
    this.applyNow(() => {
      this.payerFilter.set('');
      this.codeFilter.set('');
      this.searchFilter.set('');
      this.enabledFilter.set('');
    });
  }

  onPageChange(event: PageChangeEvent): void {
    this.pageIndex.set(event.pageIndex);
    this.pageSize.set(event.pageSize);
    this.load();
  }

  openAdd(): void {
    if (!this.actionGuard.ensure('cdshooks.create', 'You do not have permission to add coverage rules.')) return;
    if (this.payers().length === 0) {
      this.toast.info('Add a payer first. Each rule belongs to a payer.');
      return;
    }
    this.openDialog({ payers: this.payers(), payerId: this.payerFilter() || null }, 'added');
  }

  openEdit(rule: CdsCoverageRule): void {
    if (!this.actionGuard.ensure('cdshooks.edit', 'You do not have permission to edit coverage rules.')) return;
    this.openDialog({ payers: this.payers(), rule }, 'saved');
  }

  confirmDelete(rule: CdsCoverageRule): void {
    if (!this.actionGuard.ensure('cdshooks.delete', 'You do not have permission to delete coverage rules.')) return;
    this.dialog
      .open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
        width: '440px',
        data: {
          title: 'Delete coverage rule',
          message: `Delete the rule for code ${rule.code} of "${rule.payerName ?? 'this payer'}"? Orders with this code will no longer get its coverage requirements.`,
          confirmLabel: 'Delete',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        this.api.deleteRule(rule.id).subscribe({
          next: () => {
            this.toast.success(`Rule for ${rule.code} deleted.`);
            this.afterChange();
          },
          error: (err: HttpErrorResponse) => this.toast.error(err.message || 'Could not delete the rule.'),
        });
      });
  }

  openImport(): void {
    if (!this.actionGuard.ensure('cdshooks.edit', 'You do not have permission to import coverage rules.')) return;
    if (this.payers().length === 0) {
      this.toast.info('Add a payer first. Each rule in the file names its payer.');
      return;
    }
    this.dialog
      .open<CdsCoverageImportDialogComponent, CdsCoverageImportDialogData, CdsCoverageImportResult>(CdsCoverageImportDialogComponent, {
        width: '820px',
        disableClose: true,
        data: { payers: this.payers(), payerId: this.payerFilter() || null },
      })
      .afterClosed()
      .subscribe(result => {
        if (!result) return;
        const parts = [`${result.added} added`, `${result.updated} updated`];
        if (result.deleted) parts.push(`${result.deleted} deleted`);
        this.toast.success(`Rules imported: ${parts.join(', ')}.`);
        this.afterChange();
      });
  }

  /** Downloads the rules (the payer filter's payer, or all) in the import format. */
  exportRules(format: CdsCoverageFileFormat): void {
    this.api.exportRules(format, this.payerFilter() || null).subscribe({
      next: file => cdsSaveFile(file),
      error: (err: HttpErrorResponse) => this.toast.error(err.message || 'Could not export the rules.'),
    });
  }

  docLine(rule: CdsCoverageRule): string {
    if (!rule.docNeeded) return '';
    const purposes = rule.docPurposes.map(p => cdsDocPurposeLabel(p).toLowerCase());
    return purposes.length ? `${cdsDocNeededLabel(rule.docNeeded)}: ${purposes.join(', ')}` : cdsDocNeededLabel(rule.docNeeded);
  }

  infoLine(rule: CdsCoverageRule): string {
    return rule.infoNeeded.length ? `Needs: ${rule.infoNeeded.map(i => cdsInfoNeededLabel(i).toLowerCase()).join(', ')}` : '';
  }

  datesLabel(rule: CdsCoverageRule): string {
    const from = rule.effectiveFrom === '2000-01-01' ? null : rule.effectiveFrom;
    if (!from && !rule.effectiveTo) return 'Always';
    if (from && rule.effectiveTo) return `${from} to ${rule.effectiveTo}`;
    return from ? `From ${from}` : `Until ${rule.effectiveTo}`;
  }

  private applyNow(change: () => void): void {
    change();
    this.pageIndex.set(0);
    this.load();
  }

  private afterChange(): void {
    this.load();
    this.changed.emit();
  }

  private openDialog(data: CdsCoverageRuleDialogData, verb: string): void {
    this.dialog
      .open<CdsCoverageRuleDialogComponent, CdsCoverageRuleDialogData, CdsCoverageRule>(CdsCoverageRuleDialogComponent, {
        width: '720px',
        disableClose: true,
        data,
      })
      .afterClosed()
      .subscribe(saved => {
        if (!saved) return;
        this.toast.success(`Rule for ${saved.code} ${verb}.`);
        this.afterChange();
      });
  }
}
