import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink, ActivatedRoute } from '@angular/router';
import { AuthService } from '../../services/auth.service';
import { AuthCardComponent } from '../../components/auth-card/auth-card.component';
import { PasswordStrengthComponent } from '../../components/password-strength/password-strength.component';

@Component({
  selector: 'app-reset-password',
  standalone: true,
  imports: [FormsModule, RouterLink, AuthCardComponent, PasswordStrengthComponent],
  templateUrl: './reset-password.component.html',
  styleUrl: './reset-password.component.scss',
})
export class ResetPasswordComponent {
  private readonly auth    = inject(AuthService);
  private readonly route   = inject(ActivatedRoute);

  private readonly token = this.route.snapshot.queryParamMap.get('token') ?? '';

  protected newPassword     = '';
  protected confirmPassword = '';
  protected loading         = signal(false);
  protected done            = signal(false);
  protected error           = signal<string | null>(null);

  protected readonly mismatch = computed(() =>
    !!this.confirmPassword && this.newPassword !== this.confirmPassword
  );

  submit(): void {
    if (!this.token) { this.error.set('Invalid or expired reset link.'); return; }
    if (this.mismatch()) return;
    if (this.newPassword.length < 8) {
      this.error.set('Password must be at least 8 characters.');
      return;
    }
    this.loading.set(true);
    this.error.set(null);
    this.auth.resetPassword({
      token:           this.token,
      newPassword:     this.newPassword,
      confirmPassword: this.confirmPassword,
    }).subscribe({
      next: () => { this.loading.set(false); this.done.set(true); },
      error: (e) => {
        this.loading.set(false);
        this.error.set(e?.error?.message ?? 'Reset failed. The link may have expired.');
      },
    });
  }
}
