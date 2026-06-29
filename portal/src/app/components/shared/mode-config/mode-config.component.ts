import { Component, input, output, computed, signal, effect } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ModeConfigSchema, ModeConfigField } from '../../../models/mode-config.model';
import { EPIC_MODE_CONFIG } from '../../../data/mode-configs.data';

@Component({
  selector: 'app-mode-config',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './mode-config.component.html',
  styleUrl: './mode-config.component.scss',
})
export class ModeConfigComponent {
  readonly mode    = input<string>('search');
  readonly context = input<string>('');
  readonly saved   = input<Record<string, string>>({});

  readonly valuesChange = output<Record<string, string>>();

  protected values = signal<Record<string, string>>({});

  protected readonly schema = computed<ModeConfigSchema | null>(
    () => EPIC_MODE_CONFIG[this.mode()] ?? null
  );

  protected readonly isPatient = computed(() => this.context().startsWith('Patient'));

  protected readonly visibleFields = computed<ModeConfigField[]>(() => {
    const s = this.schema();
    if (!s) return [];
    return s.fields.filter(f => !(f.patientHide && this.isPatient()));
  });

  constructor() {
    effect(() => {
      const schema = this.schema();
      const savedVals = this.saved();
      if (!schema) { this.values.set({}); return; }
      const init: Record<string, string> = {};
      schema.fields.forEach(f => {
        init[f.key] = savedVals[f.key] ?? this.resolveDef(f.def);
      });
      this.values.set(init);
    });
  }

  private resolveDef(def: string | (() => string)): string {
    const raw = typeof def === 'function' ? def() : (def ?? '');
    return raw.replace('{lastRunIso}', new Date(Date.now() - 86400000).toISOString());
  }

  protected fieldValue(key: string): string {
    return this.values()[key] ?? '';
  }

  protected updateField(key: string, value: string): void {
    this.values.update(v => ({ ...v, [key]: value }));
    this.valuesChange.emit(this.values());
  }

  protected isHidden(field: ModeConfigField): boolean {
    if (!field.showIf) return false;
    return this.values()[field.showIf.key] !== field.showIf.equals;
  }

  protected schemaTitle(): string {
    return this.schema()?.title ?? '';
  }

  protected schemaNote(): string {
    const note = this.schema()?.note;
    return note ? note(this.context()) : '';
  }

  protected schemaCallouts() {
    return this.schema()?.callouts ?? [];
  }
}
