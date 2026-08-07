import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatSelectModule } from '@angular/material/select';
import { MatInputModule } from '@angular/material/input';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatCheckboxModule } from '@angular/material/checkbox';
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
  imports: [CommonModule, FormsModule, MatSelectModule, MatInputModule, MatFormFieldModule, MatCheckboxModule],
  template: `
    <div class="rule-config-form">
      @for (field of schema?.fields ?? []; track field.key) {
        @switch (field.inputKind) {
          @case ('select') {
            <mat-form-field appearance="outline" class="config-control">
              <mat-label>{{ field.label }}</mat-label>
              <mat-select [ngModel]="config[field.key] ?? field.defaultValue" (ngModelChange)="setValue(field.key, $event)">
                @for (opt of field.options ?? []; track opt) {
                  <mat-option [value]="opt">{{ opt }}</mat-option>
                }
              </mat-select>
            </mat-form-field>
          }
          @case ('checkbox') {
            <mat-checkbox
              class="config-control config-control--checkbox"
              [ngModel]="(config[field.key] ?? field.defaultValue) === 'true'"
              (ngModelChange)="setValue(field.key, $event ? 'true' : 'false')"
            >{{ field.label }}</mat-checkbox>
          }
          @default {
            <mat-form-field appearance="outline" class="config-control">
              <mat-label>{{ field.label }}</mat-label>
              <input
                matInput
                [ngModel]="config[field.key] ?? field.defaultValue ?? ''"
                (ngModelChange)="setValue(field.key, $event)"
                [placeholder]="field.placeholder ?? ''"
              />
            </mat-form-field>
          }
        }
      }
      @if (!schema || schema.fields.length === 0) {
        <p class="no-config">This node type needs no configuration.</p>
      }
    </div>
  `,
  styles: [`
    .rule-config-form { display: flex; flex-wrap: wrap; gap: 8px 12px; align-items: center; width: 100%; }
    .config-control { flex: 1 1 200px; }
    .config-control--checkbox { flex-basis: 100%; font-size: 13px; }
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
