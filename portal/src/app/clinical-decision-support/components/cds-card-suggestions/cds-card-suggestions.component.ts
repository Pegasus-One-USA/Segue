import { Component, effect, input, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { CdsAction, CdsSuggestion } from '../../models/cds.models';
import { cdsFixResourceLabel } from '../../models/cds-labels';

/** What a create action would add, read from its resource for the preview. */
interface CreatePreview {
  what: string;
  code: string | null;
  system: string | null;
  display: string | null;
  status: string | null;
  json: string;
}

/**
 * A card's one-click fixes, drawn as tickable buttons the way an EHR offers them. Ticking one shows exactly what the
 * EHR would create or delete. Nothing is sent anywhere from here: this is a preview for the Test page.
 */
@Component({
  selector: 'app-cds-card-suggestions',
  standalone: true,
  imports: [MatIconModule],
  templateUrl: './cds-card-suggestions.component.html',
  styleUrl: './cds-card-suggestions.component.scss',
})
export class CdsCardSuggestionsComponent {
  readonly suggestions = input.required<CdsSuggestion[]>();

  /** Indexes of the ticked fixes. Starts with the recommended ones, as the EHR pre-selects them. */
  readonly ticked = signal<ReadonlySet<number>>(new Set());

  constructor() {
    effect(() => {
      const recommended = new Set<number>();
      this.suggestions().forEach((s, i) => { if (s.isRecommended) recommended.add(i); });
      this.ticked.set(recommended);
    });
  }

  isTicked(index: number): boolean {
    return this.ticked().has(index);
  }

  toggle(index: number): void {
    this.ticked.update(current => {
      const next = new Set(current);
      if (next.has(index)) next.delete(index); else next.add(index);
      return next;
    });
  }

  actionsOf(suggestion: CdsSuggestion): CdsAction[] {
    return suggestion.actions ?? [];
  }

  isDelete(action: CdsAction): boolean {
    return action.type === 'delete';
  }

  /** "MedicationRequest/123" → "medication order MedicationRequest/123". */
  deleteTarget(action: CdsAction): string {
    const id = action.resourceId ?? '';
    const type = id.split('/')[0];
    return `${cdsFixResourceLabel(type)} ${id}`.trim();
  }

  createPreview(action: CdsAction): CreatePreview {
    const resource = action.resource ?? {};
    const type = typeof resource['resourceType'] === 'string' ? (resource['resourceType'] as string) : '';
    const coding = firstCoding(resource['code']) ?? firstCoding(resource['medicationCodeableConcept']);
    const status = typeof resource['status'] === 'string' ? (resource['status'] as string) : null;
    return {
      what: cdsFixResourceLabel(type),
      code: coding?.code ?? null,
      system: coding?.system ?? null,
      display: coding?.display ?? null,
      status,
      json: JSON.stringify(resource, null, 2),
    };
  }
}

function firstCoding(concept: unknown): { code?: string; system?: string; display?: string } | null {
  if (!concept || typeof concept !== 'object') return null;
  const coding = (concept as { coding?: unknown }).coding;
  if (!Array.isArray(coding) || coding.length === 0) return null;
  const first = coding[0] as Record<string, unknown>;
  const text = (key: string) => (typeof first[key] === 'string' ? (first[key] as string) : undefined);
  return { code: text('code'), system: text('system'), display: text('display') };
}
