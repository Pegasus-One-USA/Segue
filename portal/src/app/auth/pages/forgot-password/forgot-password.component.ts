import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../services/auth.service';
import { AuthCardComponent } from '../../components/auth-card/auth-card.component';

@Component({
  selector: 'app-forgot-password',
  standalone: true,
  imports: [FormsModule, RouterLink, AuthCardComponent],
  templateUrl: './forgot-password.component.html',
  styleUrl: './forgot-password.component.scss',
})
export class ForgotPasswordComponent {
  private readonly auth = inject(AuthService);

  protected email   = '';
  protected sent    = signal(false);
  protected loading = signal(false);
  protected error   = signal<string | null>(null);

  submit(): void {
    if (!this.email.trim()) return;
    this.loading.set(true);
    this.error.set(null);
    this.auth.forgotPassword({ email: this.email.trim() }).subscribe({
      next: () => { this.loading.set(false); this.sent.set(true); },
      error: (e) => {
        this.loading.set(false);
        this.error.set(e?.error?.message ?? 'Request failed. Try again.');
      },
    });
  }
}
