import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TransformNodeSchema } from '../transformation-rules.service';

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

/**
 * Renders the right control (dropdown/text/checkbox) per config key a node type reads, instead of a raw
 * JSON textarea — driven entirely by the schema TransformationRulesService.getNodeSchemas() returns, so a
 * 21st node type never needs a new form written by hand here.
 */
@Component({
  selector: 'app-rule-config-form',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="rule-config-form">
      @for (field of schema?.fields ?? []; track field.key) {
        @switch (field.inputKind) {
          @case ('select') {
            <div class="config-control">
              <label [for]="'rcf-' + field.key">{{ field.label }}</label>
              <select [id]="'rcf-' + field.key" [ngModel]="config[field.key] ?? field.defaultValue" (ngModelChange)="setValue(field.key, $event)">
                @for (opt of field.options ?? []; track opt) {
                  <option [value]="opt">{{ opt }}</option>
                }
              </select>
            </div>
          }
          @case ('checkbox') {
            <label class="config-control config-control--checkbox">
              <input
                type="checkbox"
                [ngModel]="(config[field.key] ?? field.defaultValue) === 'true'"
                (ngModelChange)="setValue(field.key, $event ? 'true' : 'false')"
              />
              {{ field.label }}
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
  // Vertical label-above-input, per the app's own form spec (§8.3) — matches mapping-profile-dialog's
  // .mpd-meta-field convention rather than Material's floating-label outline, which this dynamic,
  // schema-driven form (rendered inline inside both the per-column Rules dialog and the Transformation
  // Rules page's step editor) previously used.
  //
  // :host + .rule-config-form both display: contents — this component renders inline next to a sibling
  // "Node type" field owned by its HOST (step-card__fields, a flex-wrap row in both consumers). Giving
  // .config-control its own matching flex-wrap row here instead just creates a SECOND, separate flex
  // context nested one level down — getting every CSS value (label height, gap, flex-basis) to match the
  // host's row byte-for-byte is fragile, and any tiny drift between the two shows up as a field that's a
  // few pixels off from its true siblings. display: contents removes both this component's host element
  // and its own wrapping div from the box tree entirely, so .config-control (and the checkbox/no-config
  // fallback) become genuine flex ITEMS of the host's own step-card__fields row — one flex context, not
  // two kept in sync by hand.
  styles: [`
    :host { display: contents; }
    .rule-config-form { display: contents; }

    .config-control {
      display: flex;
      flex-direction: column;
      gap: 4px;
      flex: 1 1 200px;

      // display: block + min-height (not just font-size) — matches the outer "Node type" field's own
      // label sizing exactly (see transformation-rule-list/transform-rules-dialog's .form-field label) —
      // both sit as siblings in the same wrapping row, so a two-line label here (e.g. "Sentinel values
      // treated as empty (comma-separated)") must reserve the identical height as a shorter neighbor's
      // one-line label, or their inputs land at different Y positions.
      label {
        display: block;
        min-height: 34px;
        font-size: 13px;
        font-weight: 600;
        line-height: 1.3;
        color: var(--color-ink-2);
      }

      select, input {
        height: 42px;
        padding: 0 12px;
        border: 1.5px solid var(--color-border-input);
        border-radius: var(--radius-base);
        font-size: 13.5px;
        color: var(--color-ink);
        background-color: var(--color-surface);
        box-sizing: border-box;
        transition: border-color var(--transition-base, 150ms ease);

        &:hover { border-color: var(--color-muted); }
        &:focus-visible {
          outline: none;
          border-color: var(--color-primary);
          box-shadow: 0 0 0 3px var(--color-primary-mid);
        }
      }

      // Matches "Node type"'s own select exactly (see transform-rules-dialog/transformation-rule-list's
      // .form-field select) — same custom chevron, same reset of the browser's own native arrow, so this
      // field and its sibling "Node type" render the identical dropdown glyph in the identical spot
      // instead of each browser drawing its own.
      select {
        appearance: none;
        -webkit-appearance: none;
        -moz-appearance: none;
        cursor: pointer;
        padding-right: 32px;
        // A data: URI SVG can't reference a CSS custom property, so this one hex is unavoidable — it's
        // --color-muted's own value (#64748B), not a stand-in for a token that could be used instead.
        background-image: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 12 8'%3E%3Cpath fill='%2364748B' d='M6 8 0 0h12z'/%3E%3C/svg%3E");
        background-repeat: no-repeat;
        background-position: right 12px center;
        background-size: 10px 7px;
      }
    }

    .config-control--checkbox {
      flex-basis: 100%;
      flex-direction: row;
      align-items: center;
      gap: 8px;
      font-size: 13px;
      font-weight: 500;
      color: var(--color-ink-2);
      cursor: pointer;

      input[type="checkbox"] {
        width: 16px;
        height: 16px;
        margin: 0;
        accent-color: var(--color-primary);
      }
    }

    .no-config { font-size: 12.5px; color: var(--color-muted); margin: 0; }
  `],
})
export class RuleConfigFormComponent {
  @Input() schema: TransformNodeSchema | undefined;
  @Input() config: Record<string, string> = {};

  setValue(key: string, value: string): void {
    this.config[key] = value;
  }
}
