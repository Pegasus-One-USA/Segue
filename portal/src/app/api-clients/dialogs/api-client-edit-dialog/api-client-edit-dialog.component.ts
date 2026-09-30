import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { IApiClientService } from '../../services/i-api-client.service';
import { ApiClient } from '../../models/api-client.model';
import { DIALOG_DATA, DialogRef, DialogService } from '../../../core/services/dialog.service';
import {
  ApiClientReturnUrlListComponent,
  ApiClientReturnUrlListData,
} from '../../pages/api-client-return-url-list/api-client-return-url-list.component';

export interface ApiClientEditDialogData {
  client: ApiClient;
}

@Component({
  selector: 'app-api-client-edit-dialog',
  standalone: true,
  imports: [
    CommonModule, ReactiveFormsModule, MatDialogModule, MatButtonModule, MatIconModule,
    MatCheckboxModule, MatProgressSpinnerModule,
  ],
  templateUrl: './api-client-edit-dialog.component.html',
  styleUrls: ['./api-client-edit-dialog.component.scss'],
})
export class ApiClientEditDialogComponent {
  private readonly svc = inject(IApiClientService);
  private readonly fb = inject(FormBuilder);
  private readonly customDialog = inject(DialogService);
  readonly dialogRef = inject<DialogRef<ApiClient>>(DialogRef);
  readonly data: ApiClientEditDialogData = inject(DIALOG_DATA) as ApiClientEditDialogData;

  readonly submitted = signal(false);
  readonly saving = signal(false);
  readonly errorMessage = signal<string | null>(null);

  // Kept live (not just this.data.client) so a Manage Allowed Caller URLs round trip updates the count shown here
  // without closing this dialog.
  readonly client = signal(this.data.client);

  readonly form = this.fb.group({
    name: [this.data.client.name, [Validators.required, Validators.maxLength(200)]],
    isEnabled: [this.data.client.isEnabled],
  });

  hasError(ctrl: string, err: string): boolean {
    const c = this.form.get(ctrl)!;
    return c.hasError(err) && (c.touched || this.submitted());
  }

  save(): void {
    this.submitted.set(true);
    this.errorMessage.set(null);
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }

    const raw = this.form.getRawValue();
    this.saving.set(true);
    this.svc.update(this.data.client.id, { name: raw.name!.trim(), isEnabled: !!raw.isEnabled }).subscribe({
      next: client => {
        this.saving.set(false);
        this.dialogRef.close(client);
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.errorMessage.set(err.error?.error ?? err.error?.title ?? err.error?.message ?? 'Failed to update the API client. Please try again.');
      },
    });
  }

  openReturnUrls(): void {
    this.customDialog
      .open<ApiClientReturnUrlListComponent, ApiClientReturnUrlListData, ApiClient>(
        ApiClientReturnUrlListComponent,
        { fillContent: true, maximizable: true, disableClose: true, data: { client: this.client() } },
      )
      .afterClosed()
      .subscribe(client => {
        if (client) this.client.set(client);
      });
  }
}
