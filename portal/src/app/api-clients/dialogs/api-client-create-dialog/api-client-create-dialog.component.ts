import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { IApiClientService } from '../../services/i-api-client.service';
import { ApiClientCredential } from '../../models/api-client.model';
import { DialogRef } from '../../../core/services/dialog.service';

@Component({
  selector: 'app-api-client-create-dialog',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule, MatDialogModule, MatButtonModule, MatIconModule, MatProgressSpinnerModule],
  templateUrl: './api-client-create-dialog.component.html',
  styleUrls: ['./api-client-create-dialog.component.scss'],
})
export class ApiClientCreateDialogComponent {
  private readonly svc = inject(IApiClientService);
  private readonly fb = inject(FormBuilder);
  readonly dialogRef = inject<DialogRef<ApiClientCredential>>(DialogRef);

  readonly submitted = signal(false);
  readonly saving = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly form = this.fb.group({
    name: ['', [Validators.required, Validators.maxLength(200)]],
  });

  hasError(ctrl: string, err: string): boolean {
    const c = this.form.get(ctrl)!;
    return c.hasError(err) && (c.touched || this.submitted());
  }

  save(): void {
    this.submitted.set(true);
    this.errorMessage.set(null);
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }

    this.saving.set(true);
    this.svc.create({ name: this.form.getRawValue().name!.trim() }).subscribe({
      next: credential => {
        this.saving.set(false);
        this.dialogRef.close(credential);
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.errorMessage.set(err.error?.error ?? err.error?.title ?? err.error?.message ?? 'Failed to create the API client. Please try again.');
      },
    });
  }
}
