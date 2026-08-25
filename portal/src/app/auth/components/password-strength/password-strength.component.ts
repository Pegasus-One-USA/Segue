// auth/components/password-strength/password-strength.component.ts
import { Component, computed, input } from '@angular/core';
import { CommonModule } from '@angular/common';

interface PasswordRule {
  label: string;
  met: boolean;
}

@Component({
  selector: 'app-password-strength',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './password-strength.component.html',
  styleUrl: './password-strength.component.scss',
})
export class PasswordStrengthComponent {
  readonly password = input<string>('');

  // Mirrors LocalAuthService.ValidatePassword (backend) exactly — 12 chars minimum, all four character
  // classes. Kept in sync manually since the frontend and backend are separate codebases; if the backend
  // policy ever changes, this list (and each page's own passwordStrengthValidator) needs updating too.
  readonly rules = computed<PasswordRule[]>(() => {
    const p = this.password();
    return [
      { label: 'At least 12 characters',      met: p.length >= 12 },
      { label: 'One uppercase letter',        met: /[A-Z]/.test(p) },
      { label: 'One lowercase letter',        met: /[a-z]/.test(p) },
      { label: 'One number',                  met: /[0-9]/.test(p) },
      { label: 'One special character',       met: /[^A-Za-z0-9]/.test(p) },
    ];
  });

  readonly strength = computed<number>(() =>
    this.rules().filter(r => r.met).length
  );

  // Thresholds rebalanced for 5 possible rules (was 4) so "Strong"/full bars still means "meets every
  // backend rule", not "meets 4 of 5 and we stopped counting".
  readonly strengthLabel = computed<string>(() => {
    const s = this.strength();
    if (s <= 1) return 'Weak';
    if (s <= 2) return 'Fair';
    if (s <= 4) return 'Good';
    return 'Strong';
  });

  readonly strengthColor = computed<string>(() => {
    const s = this.strength();
    if (s <= 1) return '#EF4444';
    if (s <= 2) return '#F59E0B';
    if (s <= 4) return '#10B981';
    return '#00A89D';
  });

  readonly bars = computed<boolean[]>(() =>
    [1, 2, 3, 4, 5].map(i => i <= this.strength())
  );

  readonly levelClass = computed<string>(() => {
    const s = this.strength();
    if (s <= 1) return 'weak';
    if (s <= 2) return 'fair';
    if (s <= 4) return 'good';
    return 'strong';
  });
}
