import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatIconModule } from '@angular/material/icon';
import { TransformConfigFieldSchema, TransformNodeSchema } from '../transformation-rules.service';

/** Whether a schema field applies given the config's current values — false for a field scoped to another
 *  field's value (see TransformConfigFieldSchema.visibleWhen) when that value isn't currently selected, e.g.
 *  keepLength while mode is "hash". A field with no visibleWhen always applies. */
export function isConfigFieldVisible(field: TransformConfigFieldSchema, config: Record<string, string>): boolean {
  const rule = field.visibleWhen;
  if (!rule) return true;
  return rule.values.includes(config[rule.key]);
}

/** Merges a node type's schema defaults into an existing config object — any key the config doesn't
 *  already have gets the schema's default; keys the config already sets (e.g. loaded from a saved rule)
 *  are left untouched. Call this whenever the node type changes (new step, or switching an existing one).
 *
 *  Fields that don't apply to the current mode are neither defaulted nor kept: seeding every default
 *  regardless is what produced configs like "mode = hash, keepLength = 4, token = redcted", where two of the
 *  three keys do nothing. Because defaults are resolved in schema order, the field a visibleWhen points at
 *  (e.g. mode) is already settled by the time the fields scoped to it are considered. */
export function applyNodeDefaults(schema: TransformNodeSchema | undefined, config: Record<string, string>): Record<string, string> {
  if (!schema) return config;
  const merged = { ...config };
  for (const field of schema.fields) {
    if (merged[field.key] === undefined && field.defaultValue !== undefined && field.defaultValue !== null) {
      merged[field.key] = field.defaultValue;
    }
  }
  return pruneInapplicableConfig(schema, merged);
}

/** Drops every key whose field doesn't apply to the config's current mode — so switching mode after typing
 *  a token doesn't silently carry that token into a rule it has no effect on. */
export function pruneInapplicableConfig(
  schema: TransformNodeSchema | undefined, config: Record<string, string>): Record<string, string> {
  if (!schema) return config;
  const pruned = { ...config };
  for (const field of schema.fields) {
    if (!isConfigFieldVisible(field, pruned)) delete pruned[field.key];
  }
  return pruned;
}

const CUSTOM_SENTINEL = '__custom__';

interface KeyValueRow {
  key: string;
  value: string;
}

/**
 * Renders the right control (dropdown/text/checkbox/combo) per config key a node type reads, instead of a raw
 * JSON textarea — driven entirely by the schema TransformationRulesService.getNodeSchemas() returns, so a
 * 21st node type never needs a new form written by hand here.
 */
@Component({
  selector: 'app-rule-config-form',
  standalone: true,
  imports: [CommonModule, FormsModule, MatIconModule],
  template: `
    <div class="rule-config-form" [class.rule-config-form--inline]="inline">
      @for (field of (inline ? allFields() : primaryFields()); track field.key) {
        <ng-container *ngTemplateOutlet="fieldControl; context: { $implicit: field }" />
      }

      @if (!inline && advancedFields().length > 0) {
        <button type="button" class="advanced-toggle" (click)="toggleAdvanced()" [attr.aria-expanded]="advancedOpen">
          <mat-icon>{{ advancedOpen ? 'expand_less' : 'chevron_right' }}</mat-icon>
          Advanced Options ({{ advancedFields().length }})
        </button>
        @if (advancedOpen) {
          <div class="advanced-fields">
            @for (field of advancedFields(); track field.key) {
              <ng-container *ngTemplateOutlet="fieldControl; context: { $implicit: field }" />
            }
          </div>
        }
      }

      @if (!schema || schema.fields.length === 0) {
        <p class="no-config">This node type needs no configuration.</p>
      }
    </div>

    <ng-template #fieldControl let-field>
      @switch (field.inputKind) {
          @case ('number') {
            <div class="config-control">
              <label [for]="'rcf-' + field.key">{{ field.label }}</label>
              <input
                type="number"
                [id]="'rcf-' + field.key"
                [ngModel]="config[field.key] ?? field.defaultValue ?? ''"
                (ngModelChange)="setValue(field.key, $event)"
                [placeholder]="field.placeholder ?? ''"
              />
            </div>
          }
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
          @case ('keyvalue') {
            <div class="config-control config-control--keyvalue">
              <label>{{ field.label }}</label>
              <div class="keyvalue-table">
                @for (row of mapRowsFor(field); track $index) {
                  <div class="keyvalue-row">
                    <input placeholder="From" [ngModel]="row.key" (ngModelChange)="setMapRowKey(field, $index, $event)" />
                    <mat-icon class="keyvalue-arrow">arrow_forward</mat-icon>
                    <input placeholder="To" [ngModel]="row.value" (ngModelChange)="setMapRowValue(field, $index, $event)" />
                    <button type="button" class="keyvalue-remove" (click)="removeMapRow(field, $index)" aria-label="Remove row">
                      <mat-icon>close</mat-icon>
                    </button>
                  </div>
                }
                <button type="button" class="keyvalue-add" (click)="addMapRow(field)">
                  <mat-icon>add</mat-icon> Add row
                </button>
              </div>
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
    </ng-template>
  `,
  styleUrls: ['./rule-config-form.component.scss'],
})
export class RuleConfigFormComponent {
  @Input() schema: TransformNodeSchema | undefined;
  @Input() config: Record<string, string> = {};
  /** Fires after every edit, carrying the same object `config` points at. The host already holds that
   *  reference and saves from it, so this is not how the value reaches the save — it exists so a host that
   *  DERIVES something from the config (the join popover gates its instance picker on mode === "split") is
   *  told when to re-derive. Mutating in place is invisible to a signal or an OnPush parent otherwise. */
  @Output() readonly configChange = new EventEmitter<Record<string, string>>();
  /** Renders every applicable field as one wrapping row with no "Advanced Options" collapse, for a host
   *  form that wants its controls on a single line. Only sensible for a short schema — the de-identification
   *  rule's mode plus whichever single field that mode uses. Left false everywhere else, where the default
   *  grid-plus-collapse keeps a long schema readable. */
  @Input() inline = false;
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

  /** Starts collapsed every time — a fresh component instance per step/rule (same reasoning as
   *  forcedCustomKeys below), so there's no stale "left open" state to restore between different rules. */
  advancedOpen = false;

  toggleAdvanced(): void {
    this.advancedOpen = !this.advancedOpen;
  }

  /** Checkbox fields always render last, after every text/select field within whichever group (primary or
   *  advanced) they belong to — a lone toggle reads better as a trailing "also do this" option than sitting
   *  wherever the schema happened to declare it. Reordering the actual array (not a CSS order: override)
   *  keeps DOM/tab order in sync with visual order. */
  private ordered(fields: TransformConfigFieldSchema[]): TransformConfigFieldSchema[] {
    const rest = fields.filter(f => f.inputKind !== 'checkbox');
    const checkboxes = fields.filter(f => f.inputKind === 'checkbox');
    return [...rest, ...checkboxes];
  }

  /** Everything the schema doesn't mark isAdvanced — shown up front, no extra click needed. */
  primaryFields(): TransformConfigFieldSchema[] {
    return this.ordered(this.applicableFields().filter(f => !f.isAdvanced));
  }

  /** Fine-tuning/edge-case fields (see TransformConfigFieldSchema.isAdvanced) — tucked behind the
   *  "Advanced Options" toggle so the primary form stays short for the common case. */
  advancedFields(): TransformConfigFieldSchema[] {
    return this.ordered(this.applicableFields().filter(f => f.isAdvanced));
  }

  /** Every applicable field, advanced or not — inline mode has no collapse to hide any of them behind.
   *  With visibleWhen now scoping mode-specific fields, "advanced" no longer means "usually irrelevant"
   *  for this schema: whatever is left is exactly what the chosen mode uses. */
  allFields(): TransformConfigFieldSchema[] {
    return this.ordered(this.applicableFields());
  }

  /** The schema's fields minus those scoped to a mode that isn't selected — e.g. keepLength disappears the
   *  moment mode switches off "mask", rather than sitting there implying it still does something. */
  private applicableFields(): TransformConfigFieldSchema[] {
    return (this.schema?.fields ?? []).filter(f => isConfigFieldVisible(f, this.config));
  }

  setValue(key: string, value: string | number | null): void {
    // A type="number" input's ngModelChange fires a real JS number (Angular's NumberValueAccessor), not a
    // string — but `config` is a Record<string,string> serialized straight into the save request, so an
    // un-stringified number goes out as a bare JSON number and the API rejects it (config must bind as
    // Dictionary<string,string>). Coerce everything through this one path instead of trusting the caller.
    this.config[key] = value === null || value === undefined ? '' : String(value);

    // Changing a field others are scoped to (mode) must drop their now-inapplicable values, or a token
    // typed under "redact" would still be sitting in the config after switching to "hash". Mutated in
    // place because `config` is an @Input object the parent holds a reference to and saves from.
    for (const field of this.schema?.fields ?? []) {
      if (!isConfigFieldVisible(field, this.config)) delete this.config[field.key];
    }

    // And a field that survives the switch but no longer means what it did must be cleared too. Visibility
    // cannot express that case: ConcatenationTemplating's template applies in both modes, binding "{0} {1}"
    // to a joined column's source fields under concat and to a split's own pieces under split — so carrying
    // one across looked like the setting had been kept when it had really been re-pointed at other inputs.
    for (const field of this.schema?.fields ?? []) {
      if (field.resetOn?.includes(key)) delete this.config[field.key];
    }

    this.configChange.emit(this.config);
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

  // Cached per field key alongside the raw JSON string it was parsed from, so a keystroke in one row
  // doesn't reparse the whole table on every change detection pass, while an external change to
  // config[field.key] (e.g. switching this step's node type, or loading a different saved rule into this
  // same component instance) is still picked up — detected by the raw string no longer matching what's
  // cached, which re-parses from scratch rather than silently showing stale rows.
  private readonly mapRowsCache = new Map<string, { raw: string; rows: KeyValueRow[] }>();

  mapRowsFor(field: TransformConfigFieldSchema): KeyValueRow[] {
    const raw = this.config[field.key] ?? field.defaultValue ?? '{}';
    const cached = this.mapRowsCache.get(field.key);
    if (cached && cached.raw === raw) {
      return cached.rows;
    }

    const rows = this.parseMapRows(raw);
    this.mapRowsCache.set(field.key, { raw, rows });
    return rows;
  }

  private parseMapRows(json: string): KeyValueRow[] {
    try {
      const parsed: unknown = JSON.parse(json);
      if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
        return Object.entries(parsed as Record<string, unknown>).map(([key, value]) => ({ key, value: String(value) }));
      }
    } catch {
      // A previously-saved value that isn't valid JSON (shouldn't happen going forward, now that this
      // field can only be edited through this table) starts fresh instead of crashing the form.
    }
    return [];
  }

  setMapRowKey(field: TransformConfigFieldSchema, index: number, key: string): void {
    this.syncMapField(field, this.mapRowsFor(field).map((row, i) => (i === index ? { ...row, key } : row)));
  }

  setMapRowValue(field: TransformConfigFieldSchema, index: number, value: string): void {
    this.syncMapField(field, this.mapRowsFor(field).map((row, i) => (i === index ? { ...row, value } : row)));
  }

  addMapRow(field: TransformConfigFieldSchema): void {
    this.syncMapField(field, [...this.mapRowsFor(field), { key: '', value: '' }]);
  }

  removeMapRow(field: TransformConfigFieldSchema, index: number): void {
    this.syncMapField(field, this.mapRowsFor(field).filter((_, i) => i !== index));
  }

  // A row whose "from" is still blank (freshly added, not yet typed into) is kept in the visible table but
  // left out of the serialized JSON object — so a half-filled new row never becomes a stray `"": "..."`
  // entry in the saved config.
  private syncMapField(field: TransformConfigFieldSchema, rows: KeyValueRow[]): void {
    const obj: Record<string, string> = {};
    for (const row of rows) {
      if (row.key !== '') obj[row.key] = row.value;
    }

    const raw = JSON.stringify(obj);
    this.setValue(field.key, raw);
    this.mapRowsCache.set(field.key, { raw, rows });
  }
}
