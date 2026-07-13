// auth/pages/forgot-password/forgot-password.component.ts
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
import { authErrorMessage } from '../../services/http-error.util';

@Component({
  selector: 'app-forgot-password',
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
  templateUrl: './forgot-password.component.html',
  styleUrl: './forgot-password.component.scss',
})
export class ForgotPasswordComponent {
  private readonly fb     = inject(FormBuilder);
  private readonly auth   = inject(AuthService);

  protected readonly sent      = signal(false);
  protected readonly loading   = signal(false);
  protected readonly error     = signal<string | null>(null);
  protected readonly submitted = signal(false);

  /** Keep a reference to the submitted email to show in success state */
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

    this.auth.forgotPassword({ email }).subscribe({
      next: () => {
        this.loading.set(false);
        this.submittedEmail = email;
        this.sent.set(true);
      },
      error: (e) => {
        this.loading.set(false);
        this.error.set(authErrorMessage(e, 'Request failed. Please try again.'));
      },
    });
  }
}
