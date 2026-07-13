import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { ReactiveFormsModule, FormBuilder, Validators, AbstractControl, ValidationErrors } from '@angular/forms';
import { startWith } from 'rxjs/operators';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { HttpErrorResponse } from '@angular/common/http';
import { PasswordPolicyService } from '../../services/password-policy.service';
import { PasswordValidation } from '../../models/password-policy.model';
import { SsoButtonsComponent } from '../../components/sso-buttons/sso-buttons.component';
import { SsoAuthApiService } from '../../services/sso-auth-api.service';
import { SsoResult } from '../../services/sso.service';
import { AuthBrandHeaderComponent } from '../../components/auth-brand-header/auth-brand-header.component';
import { authErrorMessage } from '../../services/http-error.util';

export type PageState = 'loading' | 'valid' | 'invalid' | 'expired' | 'accepted' | 'success';

function matchPasswords(group: AbstractControl): ValidationErrors | null {
  const pw  = group.get('password')?.value  as string;
  const cfm = group.get('confirm')?.value   as string;
  return pw && cfm && pw !== cfm ? { mismatch: true } : null;
}

@Component({
  selector: 'app-set-password',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    RouterLink,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    SsoButtonsComponent,
    AuthBrandHeaderComponent,
  ],
  templateUrl: './set-password.component.html',
  styleUrl: './set-password.component.scss',
})
export class SetPasswordComponent implements OnInit {
  private readonly route      = inject(ActivatedRoute);
  private readonly router     = inject(Router);
  private readonly fb         = inject(FormBuilder);
  private readonly policySvc  = inject(PasswordPolicyService);
  private readonly ssoApi     = inject(SsoAuthApiService);
  private readonly snackBar   = inject(MatSnackBar);

  protected readonly state      = signal<PageState>('loading');
  /** Email being activated, sourced from the invite link query param. */
  protected readonly email      = signal('');
  protected readonly isLoading  = signal(false);
  protected readonly submitted  = signal(false);
  protected readonly showPw     = signal(false);
  protected readonly showCfm    = signal(false);
  protected readonly serverError = signal('');
  protected readonly redirectSeconds = signal(3);

  /** Accept-invite method toggle: password (default) vs single sign-on. */
  protected readonly method = signal<'password' | 'sso'>('password');
  protected readonly ssoBusy = signal(false);

  private token = '';

  protected readonly form = this.fb.nonNullable.group({
    password: ['', [Validators.required, Validators.minLength(12), Validators.maxLength(64)]],
    confirm:  ['', Validators.required],
  }, { validators: matchPasswords });

  // Signal-backed live values for reactive computed
  protected readonly pwValue = toSignal(
    this.form.get('password')!.valueChanges.pipe(startWith('')),
    { initialValue: '' }
  );
  private readonly cfmValue = toSignal(
    this.form.get('confirm')!.valueChanges.pipe(startWith('')),
    { initialValue: '' }
  );

  protected readonly pwValidation = computed<PasswordValidation>(() =>
    this.policySvc.validate(this.pwValue())
  );

  protected readonly passwordsMatch = computed(() =>
    this.pwValue() !== '' && this.pwValue() === this.cfmValue()
  );

  protected readonly canSubmit = computed(() =>
    this.pwValidation().allMet && this.passwordsMatch()
  );

  ngOnInit(): void {
    const params = this.route.snapshot.queryParamMap;
    this.token = params.get('token') ?? '';
    this.email.set(params.get('email') ?? '');

    // There is no backend "validate token" endpoint — the token is only checked
    // when it is redeemed. So we don't block on validation: if the link carries a
    // token, show the password form directly. A missing token is the invalid state.
    if (!this.token) {
      this.state.set('invalid');
      return;
    }

    this.state.set('valid');
  }

  protected togglePw():  void { this.showPw.update(v => !v); }
  protected toggleCfm(): void { this.showCfm.update(v => !v); }

  protected setMethod(m: 'password' | 'sso'): void {
    this.serverError.set('');
    this.method.set(m);
  }

  // ─── SSO accept-invite ──────────────────────────────────────────────────────
  protected onSsoAuthenticated(result: SsoResult): void {
    const email = this.email();
    if (!email || !this.token) return;
    this.serverError.set('');
    this.ssoBusy.set(true);
    // SsoAuthApiService.establishSession stores tokens + populates AuthStore on success.
    this.ssoApi.acceptInviteViaSso(email, this.token, result.provider, result.token).subscribe({
      next: () => {
        this.ssoBusy.set(false);
        this.router.navigate(['/dashboard']);
      },
      error: (err: HttpErrorResponse) => {
        this.ssoBusy.set(false);
        const message = authErrorMessage(
          err,
          'Could not accept the invitation with that identity. Ensure the email matches your invite.'
        );
        this.serverError.set(message);
        this.snackBar.open(message, 'Dismiss', { duration: 6000, panelClass: ['snack-error'] });
      },
    });
  }

  protected onSsoFailed(message: string): void {
    this.serverError.set(message);
  }

  protected submit(): void {
    this.submitted.set(true);
    this.serverError.set('');

    if (!this.canSubmit()) return;

    const { password } = this.form.getRawValue();
    this.isLoading.set(true);

    this.ssoApi.acceptInvite(this.email(), this.token, password).subscribe({
      next: () => {
        this.isLoading.set(false);
        this.state.set('success');
        this.startRedirectCountdown();
      },
      error: (_err: HttpErrorResponse) => {
        this.isLoading.set(false);
        const message = 'This invitation is invalid or has expired.';
        this.serverError.set(message);
        this.snackBar.open(message, 'Dismiss', { duration: 6000, panelClass: ['snack-error'] });
      },
    });
  }

  private startRedirectCountdown(): void {
    const tick = setInterval(() => {
      const remaining = this.redirectSeconds() - 1;
      this.redirectSeconds.set(remaining);
      if (remaining <= 0) {
        clearInterval(tick);
        this.router.navigate(['/auth/login'], { queryParams: { activated: '1' } });
      }
    }, 1000);
  }

  protected strengthLabel(v: PasswordValidation): string {
    return { weak: 'Weak', fair: 'Fair', good: 'Good', strong: 'Strong' }[v.strength];
  }

  protected strengthWidth(v: PasswordValidation): string {
    return `${v.score}%`;
  }
}
