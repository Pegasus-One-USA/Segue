import { Component, computed, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MatIconModule } from '@angular/material/icon';
import { Observable, of, switchMap } from 'rxjs';
import { DIALOG_DATA, DialogRef, DialogService } from '../../../core/services/dialog.service';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { PermissionService } from '../../../auth/services/permission.service';
import { ToastService } from '../../../services/toast.service';
import { CdsCoverageApiService, cdsSaveFile } from '../../services/cds-coverage-api.service';
import {
  CdsCoverageFileFormat,
  CdsCoverageImportMode,
  CdsCoverageImportResult,
  CdsCoverageImportStrategy,
  CdsPayer,
} from '../../models/cds-coverage.models';
import { CdsImportPreviewComponent } from '../../components/cds-import-preview/cds-import-preview.component';

export interface CdsCoverageImportDialogData {
  payers: CdsPayer[];
  /** Pre-selects this payer (e.g. the rules list's payer filter). */
  payerId?: string | null;
}

/** Largest file the API takes. */
export const CDS_IMPORT_MAX_BYTES = 5 * 1024 * 1024;

type Step = 'choose' | 'preview';

/**
 * Import coverage rules from a CSV or JSON file in two steps: choose the file and how to import it, then preview
 * what it would do (nothing is saved) and apply it. Apply saves every row or none, so it stays off while any row has
 * an error. "Replace" deletes the payer's rules that the file does not list, so it is called out and confirmed.
 */
@Component({
  selector: 'app-cds-coverage-import-dialog',
  standalone: true,
  imports: [ReactiveFormsModule, MatIconModule, CdsImportPreviewComponent],
  templateUrl: './cds-coverage-import-dialog.component.html',
  styleUrl: './cds-coverage-import-dialog.component.scss',
})
export class CdsCoverageImportDialogComponent {
  private readonly api = inject(CdsCoverageApiService);
  private readonly dialog = inject(DialogService);
  private readonly toast = inject(ToastService);
  private readonly actionGuard = inject(PermissionActionGuard);
  private readonly permissions = inject(PermissionService);
  readonly dialogRef = inject<DialogRef<CdsCoverageImportResult>>(DialogRef);
  readonly data = inject(DIALOG_DATA) as CdsCoverageImportDialogData;

  readonly payers = this.data.payers;
  /** Replace deletes rules, so it needs the delete permission too (the API refuses it without). */
  readonly canReplace = this.permissions.hasPermission('cdshooks.delete');

  readonly options = new FormGroup({
    format: new FormControl<CdsCoverageFileFormat>('csv', { nonNullable: true }),
    payerId: new FormControl(this.data.payerId ?? '', { nonNullable: true }),
    strategy: new FormControl<CdsCoverageImportStrategy>('merge', { nonNullable: true }),
  });

  readonly step = signal<Step>('choose');
  readonly file = signal<File | null>(null);
  readonly fileError = signal<string | null>(null);
  readonly busy = signal(false);
  readonly errorMessage = signal<string | null>(null);
  readonly preview = signal<CdsCoverageImportResult | null>(null);
  readonly strategy = signal<CdsCoverageImportStrategy>('merge');

  readonly canPreview = computed(() => !!this.file() && !this.fileError() && !this.busy());
  readonly previewHasErrors = computed(() => {
    const p = this.preview();
    return !p || p.errorCount > 0 || p.fileErrors.length > 0;
  });
  readonly nothingToDo = computed(() => {
    const p = this.preview();
    return !!p && p.added + p.updated + p.deleted === 0;
  });
  readonly canApply = computed(() => !this.busy() && !this.previewHasErrors() && !this.nothingToDo());

  constructor() {
    // Any change to the choices makes an earlier preview stale.
    this.options.valueChanges.subscribe(value => {
      this.strategy.set(value.strategy ?? 'merge');
      this.backToChoose();
    });
  }

  /** Leaving after choosing a file (and not applying) asks first. */
  hasUnsavedChanges(): boolean {
    return !!this.file();
  }

  isSaveInProgress(): boolean {
    return this.busy();
  }

  onFileChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0] ?? null;
    this.file.set(file);
    this.fileError.set(null);
    this.backToChoose();
    if (!file) return;
    if (file.size === 0) {
      this.fileError.set('The file is empty.');
    } else if (file.size > CDS_IMPORT_MAX_BYTES) {
      this.fileError.set('The file is larger than 5 MB. Split it into smaller files.');
    }
    // The format follows the file name; it can still be changed by hand.
    const name = file.name.toLowerCase();
    if (name.endsWith('.json')) this.options.controls.format.setValue('json', { emitEvent: false });
    else if (name.endsWith('.csv')) this.options.controls.format.setValue('csv', { emitEvent: false });
  }

  runPreview(): void {
    if (!this.actionGuard.ensure('cdshooks.edit', 'You do not have permission to import coverage rules.')) return;
    if (!this.canPreview()) return;
    this.send('preview').subscribe({
      next: result => {
        this.busy.set(false);
        this.preview.set(result);
        this.step.set('preview');
      },
      error: (err: HttpErrorResponse) => this.fail(err, 'Could not check the file.'),
    });
  }

  apply(): void {
    if (!this.actionGuard.ensure('cdshooks.edit', 'You do not have permission to import coverage rules.')) return;
    const preview = this.preview();
    if (!preview || !this.canApply()) return;
    if (preview.strategy === 'replace'
      && !this.actionGuard.ensure('cdshooks.delete', 'Replace deletes rules, and you do not have permission to delete coverage rules.')) return;

    const confirm$: Observable<boolean | undefined> = preview.strategy === 'replace' && preview.deleted > 0
      ? this.dialog.open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
        width: '460px',
        data: {
          title: `Delete ${preview.deleted} ${preview.deleted === 1 ? 'rule' : 'rules'}?`,
          message: `Replace deletes the ${preview.deleted === 1 ? 'rule' : `${preview.deleted} rules`} in Segue that the file `
            + 'does not list, as shown in the preview. This cannot be undone. Export the rules first if you may need them.',
          confirmLabel: 'Replace and delete',
          danger: true,
        },
      }).afterClosed()
      : of(true);

    confirm$.pipe(switchMap(confirmed => {
      if (!confirmed) return of(null);
      return this.send('apply');
    })).subscribe({
      next: result => {
        if (!result) return;
        this.busy.set(false);
        if (result.applied) {
          this.file.set(null);
          this.dialogRef.close(result);
          return;
        }
        // Something changed since the preview (e.g. a payer was deleted): show the new problems.
        this.preview.set(result);
        this.errorMessage.set('Nothing was saved. The file now has problems; see the rows below.');
      },
      error: (err: HttpErrorResponse) => this.fail(err, 'Could not import the file.'),
    });
  }

  backToChoose(): void {
    this.step.set('choose');
    this.preview.set(null);
    this.errorMessage.set(null);
  }

  downloadTemplate(format: CdsCoverageFileFormat): void {
    this.api.getImportTemplate(format).subscribe({
      next: file => cdsSaveFile(file),
      error: (err: HttpErrorResponse) => this.toast.error(err.message || 'Could not download the template.'),
    });
  }

  cancel(): void {
    this.dialogRef.attemptClose();
  }

  payerName(id: string): string {
    return this.payers.find(p => p.id === id)?.name ?? '';
  }

  private send(mode: CdsCoverageImportMode): Observable<CdsCoverageImportResult> {
    const file = this.file()!;
    const v = this.options.getRawValue();
    this.busy.set(true);
    this.errorMessage.set(null);
    return this.api.importRules({ file, format: v.format, mode, strategy: v.strategy, payerId: v.payerId || null });
  }

  private fail(err: HttpErrorResponse, fallback: string): void {
    this.busy.set(false);
    this.errorMessage.set(err.message || fallback);
  }
}
