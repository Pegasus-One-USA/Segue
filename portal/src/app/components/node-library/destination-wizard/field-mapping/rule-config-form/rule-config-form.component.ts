import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TransformConfigFieldSchema, TransformNodeSchema } from '../transformation-rules.service';

/** Merges a node type's schema defaults into an existing config object — any key the config doesn't
 *  already have gets the schema's default; keys the config already sets (e.g. loaded from a saved rule)
 *  are left untouched. Call this whenever the node type changes (new step, or switching an existing one). */
export function applyNodeDefaults(schema: TransformNodeSchema | undefined, config: Record<string, string>): Record<string, string> {
  if (!schema) return config;
  const merged = { ...config };
  for (const field of schema.fields) {
    if (merged[field.key] === undefined && field.defaultValue !== undefined && field.defaultValue !== null) {
      merged[field.key] = field.defaultValue;
    }
  }
  return merged;
}

const CUSTOM_SENTINEL = '__custom__';

/**
 * Renders the right control (dropdown/text/checkbox/combo) per config key a node type reads, instead of a raw
 * JSON textarea — driven entirely by the schema TransformationRulesService.getNodeSchemas() returns, so a
 * 21st node type never needs a new form written by hand here.
 */
@Component({
  selector: 'app-rule-config-form',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="rule-config-form">
      @for (field of orderedFields(); track field.key) {
        @switch (field.inputKind) {
          @case ('select') {
            <div class="config-control">
              <label [for]="'rcf-' + field.key">{{ field.label }}</label>
              <select [id]="'rcf-' + field.key" [ngModel]="config[field.key] ?? field.defaultValue" (ngModelChange)="setValue(field.key, $event)">
                @for (opt of optionsFor(field); track opt) {
                  <option [value]="opt">{{ opt }}</option>
                }
              </select>
            </div>
          }
          @case ('combo') {
            <div class="config-control">
              <label [for]="'rcf-' + field.key">{{ field.label }}</label>
              <select [id]="'rcf-' + field.key" [ngModel]="comboSelectValue(field)" (ngModelChange)="setComboSelection(field, $event)">
                <option [value]="CUSTOM_SENTINEL">Custom…</option>
                @for (opt of field.options ?? []; track opt) {
                  <option [value]="opt">{{ opt }}</option>
                }
              </select>
              @if (isCustom(field)) {
                <input
                  [ngModel]="config[field.key] ?? ''"
                  (ngModelChange)="setValue(field.key, $event)"
                  [placeholder]="field.placeholder ?? ''"
                />
              }
            </div>
          }
          @case ('checkbox') {
            <label class="config-control config-control--checkbox">
              <span class="checkbox-visual">
                <input
                  type="checkbox"
                  class="checkbox-visual__input"
                  [ngModel]="(config[field.key] ?? field.defaultValue) === 'true'"
                  (ngModelChange)="setValue(field.key, $event ? 'true' : 'false')"
                />
                <span class="checkbox-visual__box"></span>
              </span>
              <span class="config-control--checkbox__label">{{ field.label }}</span>
            </label>
          }
          @default {
            <div class="config-control">
              <label [for]="'rcf-' + field.key">{{ field.label }}</label>
              <input
                [id]="'rcf-' + field.key"
                [ngModel]="config[field.key] ?? field.defaultValue ?? ''"
                (ngModelChange)="setValue(field.key, $event)"
                [placeholder]="field.placeholder ?? ''"
              />
            </div>
          }
        }
      }
      @if (!schema || schema.fields.length === 0) {
        <p class="no-config">This node type needs no configuration.</p>
      }
    </div>
  `,
  styleUrls: ['./rule-config-form.component.scss'],
})
export class RuleConfigFormComponent {
  @Input() schema: TransformNodeSchema | undefined;
  @Input() config: Record<string, string> = {};
  // Lets a pre-mapping (de-identification) HashingMasking rule offer its wider strategy vocabulary
  // (remove/generalizeDateToYear/generalizeZip3, matching SafeHarborDeIdentificationService) without
  // changing the shared post-mapping schema's own hash/mask/redact options. Keyed by config field key
  // (currently only "mode" needs this); absent keys fall back to the schema's own options.
  @Input() optionOverrides: Record<string, string[]> = {};

  optionsFor(field: TransformConfigFieldSchema): string[] {
    return this.optionOverrides[field.key] ?? field.options ?? [];
  }

  protected readonly CUSTOM_SENTINEL = CUSTOM_SENTINEL;

  // Once a field is explicitly switched to "Custom…", stay in custom mode even if what's typed happens to
  // match a preset string momentarily (e.g. while backspacing) — re-evaluated fresh per component instance
  // (a new step/rule), not persisted, since applyNodeDefaults() already establishes the right starting mode.
  private readonly forcedCustomKeys = new Set<string>();

  /** Checkbox fields always render last, after every text/select field — a lone toggle reads better as a
   *  trailing "also do this" option than sitting wherever the schema happened to declare it. Reordering
   *  the actual array (not a CSS order: override) keeps DOM/tab order in sync with visual order. */
  orderedFields(): TransformConfigFieldSchema[] {
    const fields = this.schema?.fields ?? [];
    const rest = fields.filter(f => f.inputKind !== 'checkbox');
    const checkboxes = fields.filter(f => f.inputKind === 'checkbox');
    return [...rest, ...checkboxes];
  }

  setValue(key: string, value: string): void {
    this.config[key] = value;
  }

  /** A combo field is in "custom" mode (dropdown shows "Custom…", text box visible) whenever its current
   *  value isn't one of the field's own presets — including blank, which is why a brand-new field with no
   *  saved value starts in custom mode rather than silently defaulting to the first preset. */
  isCustom(field: TransformConfigFieldSchema): boolean {
    const current = this.config[field.key];
    if (this.forcedCustomKeys.has(field.key)) return true;
    return !(current && (field.options ?? []).includes(current));
  }

  comboSelectValue(field: TransformConfigFieldSchema): string {
    return this.isCustom(field) ? CUSTOM_SENTINEL : this.config[field.key]!;
  }

  setComboSelection(field: TransformConfigFieldSchema, value: string): void {
    if (value === CUSTOM_SENTINEL) {
      this.forcedCustomKeys.add(field.key);
      return;
    }
    this.forcedCustomKeys.delete(field.key);
    this.setValue(field.key, value);
  }
}
