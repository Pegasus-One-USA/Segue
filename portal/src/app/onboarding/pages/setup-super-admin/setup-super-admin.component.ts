import { Component, inject, signal, computed } from '@angular/core';
import { Router } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import {
  ReactiveFormsModule, FormBuilder, Validators,
  AbstractControl, ValidationErrors,
} from '@angular/forms';
import { startWith } from 'rxjs/operators';
import { HttpErrorResponse } from '@angular/common/http';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';

import { AppInitService } from '../../services/app-init.service';
import {
  TermsAndConditionsDialogComponent,
  TermsAndConditionsDialogResult,
} from '../../components/terms-and-conditions-dialog/terms-and-conditions-dialog.component';
import { ToastService } from '../../../services/toast.service';
import { PasswordPolicyService } from '../../../auth/services/password-policy.service';
import { PasswordValidation } from '../../../auth/models/password-policy.model';
import { AuthStore } from '../../../auth/store/auth.store';
import { SessionService } from '../../../auth/services/session.service';
import { buildUserFromProfile } from '../../../auth/services/jwt-user.mapper';
import { SsoButtonsComponent } from '../../../auth/components/sso-buttons/sso-buttons.component';
import { SsoAuthApiService } from '../../../auth/services/sso-auth-api.service';
import { SsoResult } from '../../../auth/services/sso.service';

function matchPasswords(group: AbstractControl): ValidationErrors | null {
  const pw  = group.get('password')?.value        as string;
  const cfm = group.get('confirmPassword')?.value as string;
  return pw && cfm && pw !== cfm ? { mismatch: true } : null;
}

@Component({
  selector: 'app-setup-super-admin',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatCheckboxModule,
    SsoButtonsComponent,
  ],
  templateUrl: './setup-super-admin.component.html',
  styleUrl: './setup-super-admin.component.scss',
})
export class SetupSuperAdminComponent {
  private readonly router    = inject(Router);
  private readonly fb        = inject(FormBuilder);
  private readonly appInit   = inject(AppInitService);
  private readonly policySvc = inject(PasswordPolicyService);
  private readonly toast     = inject(ToastService);
  private readonly ssoApi    = inject(SsoAuthApiService);
  private readonly dialog    = inject(MatDialog);

  // Login success handling — reuse the exact login pattern (see AuthService.login).
  private readonly store    = inject(AuthStore);
  private readonly session  = inject(SessionService);

  protected readonly ssoBusy = signal(false);

  protected readonly isLoading   = signal(false);
  protected readonly submitted   = signal(false);
  protected readonly showPw      = signal(false);
  protected readonly showCfm     = signal(false);
  protected readonly serverError = signal('');

  protected readonly form = this.fb.nonNullable.group({
    firstName:       ['', [Validators.required, Validators.maxLength(80)]],
    lastName:        ['', [Validators.required, Validators.maxLength(80)]],
    email:           ['', [Validators.required, Validators.email]],
    password:        ['', [Validators.required, Validators.minLength(12), Validators.maxLength(64)]],
    confirmPassword: ['', Validators.required],
    // SMTP settings — collected here and saved enabled (IsEnabled forced true server-side) so this
    // deployable package leaves setup with email already configured, not as a separate later step.
    smtpHost:        ['', Validators.required],
    smtpPort:        [587, [Validators.required, Validators.min(1), Validators.max(65535)]],
    smtpEnableSsl:   [true],
    smtpUsername:    [''],
    smtpPassword:    [''],
    smtpFromAddress: ['', [Validators.required, Validators.email]],
    smtpFromName:    ['FHIRBridge', Validators.required],
    // Starts disabled — enabled only once the Terms and Conditions dialog reports the reader actually
    // scrolled to the end (see openTermsDialog()). A disabled control's own .valid is false (status is
    // DISABLED, not VALID), so canSubmit() below stays blocked without any extra wiring.
    acceptTerms:     [{ value: false, disabled: true }, Validators.requiredTrue],
  }, { validators: matchPasswords });

  // Signal-backed live values for reactive computed
  protected readonly pwValue = toSignal(
    this.form.get('password')!.valueChanges.pipe(startWith('')),
    { initialValue: '' }
  );
  private readonly cfmValue = toSignal(
    this.form.get('confirmPassword')!.valueChanges.pipe(startWith('')),
    { initialValue: '' }
  );

  protected readonly pwValidation = computed<PasswordValidation>(() =>
    this.policySvc.validate(this.pwValue())
  );

  protected readonly passwordsMatch = computed(() =>
    this.pwValue() !== '' && this.pwValue() === this.cfmValue()
  );

  protected readonly canSubmit = computed(() =>
    this.form.get('firstName')!.valid &&
    this.form.get('lastName')!.valid &&
    this.form.get('email')!.valid &&
    this.pwValidation().allMet &&
    this.passwordsMatch() &&
    this.form.get('smtpHost')!.valid &&
    this.form.get('smtpPort')!.valid &&
    this.form.get('smtpFromAddress')!.valid &&
    this.form.get('smtpFromName')!.valid &&
    this.form.get('acceptTerms')!.valid
  );

  protected openTermsDialog(): void {
    this.dialog
      .open<TermsAndConditionsDialogComponent, void, TermsAndConditionsDialogResult>(
        TermsAndConditionsDialogComponent, { autoFocus: false, restoreFocus: true })
      .afterClosed()
      .subscribe((result) => {
        if (result?.readToEnd) {
          this.form.get('acceptTerms')!.enable();
        }
      });
  }

  protected togglePw():  void { this.showPw.update(v => !v); }
  protected toggleCfm(): void { this.showCfm.update(v => !v); }

  protected submit(): void {
    this.submitted.set(true);
    this.serverError.set('');

    if (!this.canSubmit()) {
      this.form.markAllAsTouched();
      return;
    }

    const {
      firstName, lastName, email, password, acceptTerms,
      smtpHost, smtpPort, smtpEnableSsl, smtpUsername, smtpPassword, smtpFromAddress, smtpFromName,
    } = this.form.getRawValue();
    const displayName = `${firstName} ${lastName}`.trim();

    this.isLoading.set(true);

    this.appInit.createSuperAdmin({
      email, displayName, password, firstName, lastName, acceptTerms,
      emailSettings: {
        host: smtpHost.trim(),
        port: smtpPort,
        enableSsl: smtpEnableSsl,
        username: smtpUsername.trim() || null,
        password: smtpPassword.trim() || null,
        fromAddress: smtpFromAddress.trim(),
        fromName: smtpFromName.trim(),
      },
    }).subscribe({
      next: (res) => {
        // Reuse the exact login success handling: rebuild user from the profile DTO. Tokens are
        // already set as HttpOnly cookies by the backend (see AuthController.IssueTokenCookiesAndStrip).
        const user = buildUserFromProfile(res.profile!);
        user.mustChangePassword = res.requiresPasswordChange ?? false;

        this.store.setUser(user);
        this.session.start(user.id, false);
        this.isLoading.set(false);
        this.router.navigate(['/dashboard']);
      },
      error: (err: HttpErrorResponse) => {
        this.isLoading.set(false);
        if (err?.status === 409) {
          const msg = 'Setup already completed — please go to the sign-in page.';
          this.serverError.set(msg);
          this.toast.error(msg);
          return;
        }
        const message = err?.error?.message ?? 'Setup failed. Please try again.';
        this.serverError.set(message);
        this.toast.error(message);
      },
    });
  }

  // ─── SSO ───────────────────────────────────────────────────────────────────
  protected onSsoAuthenticated(result: SsoResult): void {
    this.serverError.set('');
    this.ssoBusy.set(true);
    // SsoAuthApiService.establishSession stores tokens + populates AuthStore on success.
    this.ssoApi.setupSuperAdminSso(result.provider, result.token).subscribe({
      next: () => {
        this.appInit.markSetupComplete();
        this.ssoBusy.set(false);
        this.router.navigate(['/dashboard']);
      },
      error: (err: HttpErrorResponse) => {
        this.ssoBusy.set(false);
        if (err?.status === 409) {
          const msg = 'Setup already completed — please go to the sign-in page.';
          this.serverError.set(msg);
          this.toast.error(msg);
          return;
        }
        const message = err?.error?.message ?? 'Setup failed. Please try again.';
        this.serverError.set(message);
        this.toast.error(message);
      },
    });
  }

  protected onSsoFailed(message: string): void {
    this.serverError.set(message);
  }

  protected strengthLabel(v: PasswordValidation): string {
    return { weak: 'Weak', fair: 'Fair', good: 'Good', strong: 'Strong' }[v.strength];
  }

  protected strengthWidth(v: PasswordValidation): string {
    return `${v.score}%`;
  }
}
