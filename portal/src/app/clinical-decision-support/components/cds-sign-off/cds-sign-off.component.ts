import { Component, inject, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MatIconModule } from '@angular/material/icon';
import { Observable, map } from 'rxjs';
import { CdsServiceInstance } from '../../models/cds.models';
import { CdsApiService } from '../../services/cds-api.service';
import { CdsSignOffBadgeComponent } from '../cds-sign-off-badge/cds-sign-off-badge.component';
import { DialogService } from '../../../core/services/dialog.service';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';

/**
 * Clinical sign-off for one saved service: records that a clinician reviewed its rules and settings, one-click fixes
 * and codes, dismiss reasons and links, or removes that record. Advisory: it never stops the service being switched
 * on. The API clears it by itself when a later save changes any of that content. Each action asks first.
 */
@Component({
  selector: 'app-cds-sign-off',
  standalone: true,
  imports: [DatePipe, ReactiveFormsModule, MatIconModule, CdsSignOffBadgeComponent],
  templateUrl: './cds-sign-off.component.html',
  styleUrl: './cds-sign-off.component.scss',
})
export class CdsSignOffComponent {
  private readonly api     = inject(CdsApiService);
  private readonly dialogs = inject(DialogService);
  private readonly toast   = inject(ToastService);

  /** The service as last saved. */
  readonly service = input.required<CdsServiceInstance>();
  /** Holds cdshooks.edit. Without it the section is read-only. */
  readonly canEdit = input(false);
  /** The setup page has unsaved edits: a sign-off covers the saved version only, so it waits for a save. */
  readonly hasUnsavedChanges = input(false);

  /** The service with its new sign-off state. */
  readonly changed = output<CdsServiceInstance>();

  readonly busy = signal(false);
  readonly submitted = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly form = new FormGroup({
    signedOffBy: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(200)] }),
    note: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(500)] }),
  });

  nameError(): boolean {
    const control = this.form.controls.signedOffBy;
    return (control.touched || this.submitted()) && control.invalid;
  }

  record(): void {
    this.submitted.set(true);
    this.errorMessage.set(null);
    const name = this.form.controls.signedOffBy.value.trim();
    if (!name) this.form.controls.signedOffBy.setValue('');
    if (this.form.invalid || this.hasUnsavedChanges() || this.busy()) {
      this.form.markAllAsTouched();
      return;
    }

    this.confirm({
      title: 'Record clinical sign-off?',
      message:
        `You are recording that ${name} reviewed "${this.service().title}" as last saved: its rules and settings, `
        + 'one-click fixes and codes, dismiss reasons and links. Any later change to these removes the sign-off.',
      confirmLabel: 'Record sign-off',
    }).subscribe(confirmed => {
      if (!confirmed) return;
      const note = this.form.controls.note.value.trim();
      const request = { signedOffBy: name, note: note || null, contentVersion: this.service().contentVersion ?? null };
      this.run(this.api.signOffService(this.service().id, request), 'Clinical sign-off recorded.');
    });
  }

  remove(): void {
    if (this.busy()) return;
    this.errorMessage.set(null);
    this.confirm({
      title: 'Remove the clinical sign-off?',
      message: `"${this.service().title}" will show as not clinically reviewed until a clinician signs it off again.`,
      confirmLabel: 'Remove sign-off',
      danger: true,
    }).subscribe(confirmed => {
      if (confirmed) this.run(this.api.clearSignOff(this.service().id), 'Clinical sign-off removed.');
    });
  }

  private run(request$: Observable<CdsServiceInstance>, message: string): void {
    this.busy.set(true);
    request$.subscribe({
      next: updated => {
        this.busy.set(false);
        this.submitted.set(false);
        this.form.reset();
        this.toast.success(message);
        this.changed.emit(updated);
      },
      error: (err: HttpErrorResponse) => {
        this.busy.set(false);
        this.errorMessage.set(err.status === 409
          ? 'Someone changed this service after you opened it, so the sign-off was not recorded. '
            + 'Reload the page, have the clinician review the new version, then record the sign-off again.'
          : err.message || 'Could not save the sign-off.');
      },
    });
  }

  private confirm(data: ConfirmDialogData): Observable<boolean> {
    return this.dialogs
      .open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, { width: '460px', data })
      .afterClosed()
      .pipe(map(confirmed => confirmed === true));
  }
}
