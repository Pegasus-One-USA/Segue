import { Injectable } from '@angular/core';
import { PasswordValidation, PasswordRequirement, PasswordStrength } from '../models/password-policy.model';

const COMMON_PASSWORDS = new Set([
  'Password123!', 'Admin123!', 'Welcome1!', 'P@ssword1', 'Qwerty123!',
  'Summer2024!', 'Winter2024!', 'Spring2024!', 'Fall2024!',
  'Segue1!', 'Hospital1!', 'Healthcare1!', 'Medical123!',
  'Abc123456!', 'Test1234!', 'Hello123!', 'Company123!',
  'Passw0rd!', 'Password1!', 'Welcome123!', 'ChangeMe1!',
]);

@Injectable({ providedIn: 'root' })
export class PasswordPolicyService {

  validate(password: string): PasswordValidation {
    if (!password) {
      return {
        requirements: this.buildRequirements(''),
        strength: 'weak',
        score: 0,
        allMet: false,
        isCommon: false,
      };
    }

    const requirements = this.buildRequirements(password);
    const isCommon     = this.isCommonPassword(password);
    const allMet       = requirements.every(r => r.met) && !isCommon;
    const score        = this.calculateScore(password, requirements, isCommon);
    const strength     = this.scoreToStrength(score);

    return { requirements, strength, score, allMet, isCommon };
  }

  meetsPolicy(password: string): boolean {
    return this.validate(password).allMet;
  }

  private buildRequirements(pw: string): PasswordRequirement[] {
    return [
      { key: 'minLength',  label: 'At least 12 characters',        met: pw.length >= 12 },
      { key: 'maxLength',  label: 'No more than 64 characters',    met: pw.length > 0 && pw.length <= 64 },
      { key: 'uppercase',  label: 'At least one uppercase letter',  met: /[A-Z]/.test(pw) },
      { key: 'lowercase',  label: 'At least one lowercase letter',  met: /[a-z]/.test(pw) },
      { key: 'number',     label: 'At least one number',            met: /\d/.test(pw) },
      { key: 'special',    label: 'At least one special character', met: /[^A-Za-z0-9]/.test(pw) },
    ];
  }

  private isCommonPassword(pw: string): boolean {
    if (COMMON_PASSWORDS.has(pw)) return true;
    // Flag patterns like "Word123!" that are structurally common
    const simple = /^[A-Z][a-z]+\d+[!@#$%]$/.test(pw) && pw.length < 14;
    return simple;
  }

  private calculateScore(pw: string, reqs: PasswordRequirement[], isCommon: boolean): number {
    let score = 0;
    if (pw.length >= 12) score += 20;
    if (pw.length >= 16) score += 10;
    if (pw.length >= 20) score += 5;
    if (reqs.find(r => r.key === 'uppercase')?.met) score += 15;
    if (reqs.find(r => r.key === 'lowercase')?.met) score += 15;
    if (reqs.find(r => r.key === 'number')?.met)    score += 15;
    if (reqs.find(r => r.key === 'special')?.met)   score += 20;
    if (isCommon) score = Math.max(0, score - 40);
    return Math.min(100, score);
  }

  private scoreToStrength(score: number): PasswordStrength {
    if (score < 30) return 'weak';
    if (score < 55) return 'fair';
    if (score < 75) return 'good';
    return 'strong';
  }
}
