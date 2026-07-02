// auth/pages/login/login.component.ts
import { Component, inject, signal, computed } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatIconModule } from '@angular/material/icon';
import { AuthService } from '../../services/auth.service';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    RouterLink,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatCheckboxModule,
    MatProgressSpinnerModule,
    MatIconModule,
  ],
  templateUrl: './login.component.html',
  styleUrl: './login.component.scss',
})
export class LoginComponent {
  private readonly fb   = inject(FormBuilder);
  protected readonly auth = inject(AuthService);

  protected readonly isLoading = this.auth.isLoading;
  protected readonly error     = this.auth.error;

  protected readonly showPassword = signal(false);
  protected readonly capsLockOn   = signal(false);
  protected readonly submitted    = signal(false);

  protected readonly form = this.fb.nonNullable.group({
    email:      ['', [Validators.required, Validators.email]],
    password:   ['', [Validators.required, Validators.minLength(8)]],
    rememberMe: [false],
  });

  protected togglePassword(): void {
    this.showPassword.update(v => !v);
  }

  protected onKeydown(e: KeyboardEvent): void {
    this.capsLockOn.set(e.getModifierState('CapsLock'));
  }

  protected submit(): void {
    this.submitted.set(true);
    if (this.form.invalid) return;

    const { email, password, rememberMe } = this.form.getRawValue();
    this.auth.login({ email: email.trim(), password, rememberMe }).subscribe();
  }

  // Helpers for template validation display
  protected fieldError(field: string, error: string): boolean {
    const ctrl = this.form.get(field);
    return !!(ctrl?.hasError(error) && (ctrl.touched || this.submitted()));
  }
}
