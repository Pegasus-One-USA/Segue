import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialogRef, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { IAllowedCorsOriginService } from '../../services/i-allowed-cors-origin.service';
import { CreateAllowedCorsOriginRequest } from '../../models/allowed-cors-origin.model';

// Origin only, mirrors the backend's ValidateAndNormalize (AllowedCorsOriginsService.cs): absolute
// http(s) URL, no path/query/fragment. The server re-validates regardless — this is fast feedback only.
const ORIGIN_PATTERN = /^https?:\/\/[^\s/]+$/i;

@Component({
  selector: 'app-allowed-cors-origin-dialog',
  standalone: true,
  imports: [
    CommonModule,
    ReactiveFormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './allowed-cors-origin-dialog.component.html',
  styleUrls: ['./allowed-cors-origin-dialog.component.scss'],
})
export class AllowedCorsOriginDialogComponent {
  private readonly svc = inject(IAllowedCorsOriginService);
  private readonly fb  = inject(FormBuilder);
  readonly dialogRef   = inject(MatDialogRef<AllowedCorsOriginDialogComponent>);

  readonly submitted    = signal(false);
  readonly saving       = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly form = this.fb.group({
    originUrl: ['', [Validators.required, Validators.pattern(ORIGIN_PATTERN), Validators.maxLength(500)]],
    label: ['', [Validators.maxLength(200)]],
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
    const request: CreateAllowedCorsOriginRequest = {
      originUrl: raw.originUrl!.trim(),
      label: raw.label?.trim() || null,
    };
    this.saving.set(true);

    this.svc.create(request).subscribe({
      next: () => {
        this.saving.set(false);
        this.dialogRef.close(true);
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.errorMessage.set(err.error?.title ?? 'Failed to add origin. Please try again.');
      },
    });
  }
}
