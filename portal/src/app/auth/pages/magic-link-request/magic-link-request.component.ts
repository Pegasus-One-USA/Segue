// auth/pages/magic-link-request/magic-link-request.component.ts
import { Component, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatIconModule } from '@angular/material/icon';
import { AuthService } from '../../services/auth.service';
import { AuthBrandHeaderComponent } from '../../components/auth-brand-header/auth-brand-header.component';

@Component({
  selector: 'app-magic-link-request',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    RouterLink,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatProgressSpinnerModule,
    MatIconModule,
    AuthBrandHeaderComponent,
  ],
  templateUrl: './magic-link-request.component.html',
  styleUrl: './magic-link-request.component.scss',
})
export class MagicLinkRequestComponent {
  private readonly fb   = inject(FormBuilder);
  private readonly auth = inject(AuthService);

  protected readonly sent      = signal(false);
  protected readonly loading   = signal(false);
  protected readonly error     = signal<string | null>(null);
  protected readonly submitted = signal(false);

  protected submittedEmail = '';

  protected readonly form = this.fb.nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
  });

  protected fieldError(field: string, errorKey: string): boolean {
    const ctrl = this.form.get(field);
    return !!(ctrl?.hasError(errorKey) && (ctrl.touched || this.submitted()));
  }

  protected submit(): void {
    this.submitted.set(true);
    this.form.markAllAsTouched();
    if (this.form.invalid) return;

    const email = this.form.getRawValue().email.trim();
    this.loading.set(true);
    this.error.set(null);

    this.auth.requestMagicLink({ email }).subscribe({
      next: () => {
        this.loading.set(false);
        this.submittedEmail = email;
        this.sent.set(true);
      },
      error: (e) => {
        this.loading.set(false);
        this.error.set(e?.error?.message ?? 'Request failed. Please try again.');
      },
    });
  }
}
