// auth/pages/magic-link-redeem/magic-link-redeem.component.ts
import { Component, OnInit, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatIconModule } from '@angular/material/icon';
import { HttpErrorResponse } from '@angular/common/http';
import { AuthService } from '../../services/auth.service';
import { extractApiErrorMessage } from '../../../core/http-error.util';
import { AuthBrandHeaderComponent } from '../../components/auth-brand-header/auth-brand-header.component';

type RedeemStage = 'redeeming' | 'mfa' | 'error';

@Component({
  selector: 'app-magic-link-redeem',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    RouterLink,
    MatFormFieldModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatIconModule,
    AuthBrandHeaderComponent,
  ],
  templateUrl: './magic-link-redeem.component.html',
  styleUrl: './magic-link-redeem.component.scss',
})
export class MagicLinkRedeemComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly auth  = inject(AuthService);
  private readonly fb    = inject(FormBuilder);

  protected readonly stage        = signal<RedeemStage>('redeeming');
  protected readonly error        = signal<string | null>(null);
  protected readonly mfaSubmitted = signal(false);
  protected readonly isLoading    = this.auth.isLoading;

  private email = '';
  private mfaChallengeToken = '';

  protected readonly mfaForm = this.fb.nonNullable.group({
    code: ['', [Validators.required, Validators.minLength(6)]],
  });

  ngOnInit(): void {
    const params = this.route.snapshot.queryParamMap;
    const email = params.get('email');
    const token = params.get('token');

    if (!email || !token) {
      this.stage.set('error');
      this.error.set('This sign-in link is missing required information.');
      return;
    }

    this.email = email;
    this.auth.redeemMagicLink({ email, token }).subscribe({
      next: (res) => {
        if (res.requiresMfa) {
          this.mfaChallengeToken = res.mfaChallengeToken;
          this.stage.set('mfa');
        }
        // Non-MFA success navigates to /dashboard from within AuthService itself.
      },
      error: (err: HttpErrorResponse) => {
        this.stage.set('error');
        this.error.set(extractApiErrorMessage(err, 'This sign-in link is invalid or has expired.'));
      },
    });
  }

  protected submitMfa(): void {
    this.mfaSubmitted.set(true);
    if (this.mfaForm.invalid) return;

    const { code } = this.mfaForm.getRawValue();
    this.auth.completeMfaLogin(this.email, this.mfaChallengeToken, code.trim()).subscribe({
      error: (err) => {
        this.error.set(extractApiErrorMessage(err, 'Invalid or expired code. Please try again.'));
      },
    });
  }

  protected mfaFieldError(errorKey: string): boolean {
    const ctrl = this.mfaForm.get('code');
    return !!(ctrl?.hasError(errorKey) && (ctrl.touched || this.mfaSubmitted()));
  }
}
