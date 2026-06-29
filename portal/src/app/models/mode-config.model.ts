export type FieldType = 'text' | 'textarea' | 'select' | 'number';

export interface ShowIfRule {
  key: string;
  equals: string;
}

export interface ModeConfigField {
  key: string;
  label: string;
  type: FieldType;
  def: string | (() => string);
  hint?: string;
  required?: boolean;
  options?: string[];
  patientHide?: boolean;
  showIf?: ShowIfRule;
}

export interface ModeCallout {
  type: 'info' | 'warn' | 'tip';
  icon: string;
  html: string;
}

export interface ModeConfigSchema {
  title: string;
  note?: (ctx: string) => string;
  callouts?: ModeCallout[];
  fields: ModeConfigField[];
}
