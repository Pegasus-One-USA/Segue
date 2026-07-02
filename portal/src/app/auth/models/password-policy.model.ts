export type PasswordStrength = 'weak' | 'fair' | 'good' | 'strong';

export interface PasswordRequirement {
  key:   string;
  label: string;
  met:   boolean;
}

export interface PasswordValidation {
  requirements: PasswordRequirement[];
  strength:     PasswordStrength;
  score:        number;   // 0–100
  allMet:       boolean;
  isCommon:     boolean;
}
