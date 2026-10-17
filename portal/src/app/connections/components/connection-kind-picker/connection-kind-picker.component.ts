import { Component, computed, input, output } from '@angular/core';
import { ConnectionKindCard } from '../../connection-row.model';

export interface ConnectionKindCardGroup {
  heading: string;
  cards: ConnectionKindCard[];
}

/**
 * The step between "New" and a connection's own form: every kind the role can create, as cards under a heading per
 * group (EHR, Database, Destinations). A card grid rather than a dropdown, so each choice is visible and described.
 * Escape or a click on the backdrop closes it.
 */
@Component({
  selector: 'app-connection-kind-picker',
  standalone: true,
  templateUrl: './connection-kind-picker.component.html',
  styleUrl: './connection-kind-picker.component.scss',
})
export class ConnectionKindPickerComponent {
  readonly title = input.required<string>();
  readonly subtitle = input('');
  readonly groups = input.required<ConnectionKindCardGroup[]>();
  /** 'src' or 'dest' — prefixes the e2e test ids. */
  readonly testIdPrefix = input.required<string>();

  readonly picked = output<ConnectionKindCard>();
  readonly closed = output<void>();

  readonly shownGroups = computed(() => this.groups().filter(g => g.cards.length > 0));

  onBackdropClick(event: MouseEvent): void {
    if (event.target === event.currentTarget) this.closed.emit();
  }
}
