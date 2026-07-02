import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { DatePipe } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, Validators, AbstractControl, ValidationErrors } from '@angular/forms';
import { startWith } from 'rxjs/operators';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { InvitationService } from '../../services/invitation.service';
import { PasswordPolicyService } from '../../services/password-policy.service';
import { Invitation } from '../../models/invitation.model';
import { PasswordValidation } from '../../models/password-policy.model';

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
    DatePipe,
    ReactiveFormsModule,
    RouterLink,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
  ],
  templateUrl: './set-password.component.html',
  styleUrl: './set-password.component.scss',
})
export class SetPasswordComponent implements OnInit {
  private readonly route      = inject(ActivatedRoute);
  private readonly router     = inject(Router);
  private readonly fb         = inject(FormBuilder);
  private readonly invSvc     = inject(InvitationService);
  private readonly policySvc  = inject(PasswordPolicyService);

  protected readonly state      = signal<PageState>('loading');
  protected readonly invitation = signal<Invitation | null>(null);
  protected readonly isLoading  = signal(false);
  protected readonly submitted  = signal(false);
  protected readonly showPw     = signal(false);
  protected readonly showCfm    = signal(false);
  protected readonly serverError = signal('');
  protected readonly redirectSeconds = signal(3);

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
    this.token = this.route.snapshot.queryParamMap.get('token') ?? '';

    if (!this.token) {
      this.state.set('invalid');
      return;
    }

    this.invSvc.validateToken(this.token).subscribe({
      next: result => {
        if (result.valid && result.invitation) {
          this.invitation.set(result.invitation);
          this.state.set('valid');
        } else {
          this.state.set(result.error === 'expired' ? 'expired'
                       : result.error === 'already_accepted' ? 'accepted'
                       : 'invalid');
        }
      },
      error: () => this.state.set('invalid'),
    });
  }

  protected togglePw():  void { this.showPw.update(v => !v); }
  protected toggleCfm(): void { this.showCfm.update(v => !v); }

  protected submit(): void {
    this.submitted.set(true);
    this.serverError.set('');

    if (!this.canSubmit()) return;

    const { password } = this.form.getRawValue();
    this.isLoading.set(true);

    this.invSvc.acceptInvitation(this.token, password).subscribe({
      next: () => {
        this.isLoading.set(false);
        this.state.set('success');
        this.startRedirectCountdown();
      },
      error: (err) => {
        this.isLoading.set(false);
        this.serverError.set(err?.message ?? 'Something went wrong. Please try again.');
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
