import { Component, computed, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { CdsCard } from '../../models/cds.models';
import { CdsCardSuggestionsComponent } from '../cds-card-suggestions/cds-card-suggestions.component';
import { CdsCardLinksComponent } from '../cds-card-links/cds-card-links.component';

const INDICATOR: Record<string, { label: string; icon: string }> = {
  critical: { label: 'Critical', icon: 'error' },
  warning: { label: 'Warning', icon: 'warning' },
  info: { label: 'Info', icon: 'info' },
};

/**
 * One CDS card drawn the way an EHR shows it: indicator colour, summary, detail, source and links. Detail is
 * markdown and is shown as plain text, so nothing in a card can inject markup into the portal.
 */
@Component({
  selector: 'app-cds-card-preview',
  standalone: true,
  imports: [MatIconModule, CdsCardSuggestionsComponent, CdsCardLinksComponent],
  templateUrl: './cds-card-preview.component.html',
  styleUrl: './cds-card-preview.component.scss',
})
export class CdsCardPreviewComponent {
  readonly card = input.required<CdsCard>();

  readonly indicator = computed(() => {
    const key = INDICATOR[this.card().indicator] ? this.card().indicator : 'info';
    return { key, ...INDICATOR[key] };
  });

  /** Only http(s) links are clickable; anything else is shown as text. */
  isSafeUrl(url: string | undefined): boolean {
    return !!url && /^https?:\/\//i.test(url);
  }
}
