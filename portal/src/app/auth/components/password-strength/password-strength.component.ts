import { Component, computed, input } from '@angular/core';

type StrengthLevel = 'weak' | 'fair' | 'strong' | 'very-strong';

@Component({
  selector: 'app-password-strength',
  standalone: true,
  imports: [],
  templateUrl: './password-strength.component.html',
  styleUrl: './password-strength.component.scss',
})
export class PasswordStrengthComponent {
  readonly password = input('');

  readonly level = computed<StrengthLevel>(() => {
    const p = this.password();
    if (p.length < 6) return 'weak';
    let score = 0;
    if (p.length >= 8)           score++;
    if (/[A-Z]/.test(p))         score++;
    if (/[0-9]/.test(p))         score++;
    if (/[^A-Za-z0-9]/.test(p)) score++;
    return (['weak', 'fair', 'strong', 'very-strong'] as const)[score] ?? 'weak';
  });

  readonly label = computed(() => ({
    'weak':        'Weak',
    'fair':        'Fair',
    'strong':      'Strong',
    'very-strong': 'Very strong',
  }[this.level()]));

  readonly bars = computed(() => {
    const count = { weak: 1, fair: 2, strong: 3, 'very-strong': 4 }[this.level()];
    return [1, 2, 3, 4].map(i => i <= count);
  });
}
