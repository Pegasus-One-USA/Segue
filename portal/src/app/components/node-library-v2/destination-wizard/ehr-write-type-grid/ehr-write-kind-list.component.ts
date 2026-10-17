import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import {
  ALWAYS_INCLUDED_NOTE,
  EhrWriteKind,
  HOLDER_ENCOUNTER_KIND_NOTE,
  NO_KIND_CHOSEN_NOTE,
  includedWhenTickedNote,
} from './ehr-write-kinds.model';

/**
 * The kinds of record under one resource type on the write-back "Resource types" step, one checkbox each. A kind that
 * is always included is ticked and locked while its type is ticked (until then it says it is sent whenever the type
 * is ticked); an optional kind is ticked by the user. A kind
 * filed on a holder encounter says, while ticked, that ticking it is what turns that encounter on.
 */
@Component({
  selector: 'app-ehr-write-kind-list',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ul class="ewkl-list">
      @for (kind of kinds(); track kind.id) {
        <li class="ewkl-item">
          <label class="ewkl-kind" [class.ewkl-kind--locked]="isLocked(kind)" [attr.data-kind]="kind.id">
            <input
              type="checkbox"
              [checked]="isChosen(kind)"
              [disabled]="isLocked(kind)"
              (change)="toggled.emit(kind.id)" />
            <span class="ewkl-label">{{ kind.label }}</span>
            @if (!kind.optional) {
              <span class="ewkl-hint" data-testid="ewkl-included-hint">{{ includedHint(kind) }}</span>
            }
          </label>
          @if (kind.holderEncounter && isChosen(kind)) {
            <span class="ewkl-hint" data-testid="ewkl-holder-note">{{ holderNote }}</span>
          }
          @if (shared()[kind.id]; as note) {
            <span class="ewkl-hint" data-testid="ewkl-shared-note">{{ note }}</span>
          }
        </li>
      }
    </ul>
    @if (typeSelected() && chosen().length === 0) {
      <span class="ewkl-warn" data-testid="ewkl-none-chosen">{{ noneChosen }}</span>
    }
  `,
  styleUrl: './ehr-write-kind-list.component.scss',
})
export class EhrWriteKindListComponent {
  readonly kinds = input<EhrWriteKind[]>([]);
  /** Ids of the kinds written (empty while the type is not ticked). */
  readonly chosen = input<string[]>([]);
  /** The type itself is ticked: its always-included kinds are then locked on. */
  readonly typeSelected = input<boolean>(false);
  /** Per kind id: what its shared switch does to the other ticked types (sharedSwitchNote). */
  readonly shared = input<Record<string, string>>({});

  readonly toggled = output<string>();

  protected readonly holderNote = HOLDER_ENCOUNTER_KIND_NOTE;
  protected readonly noneChosen = NO_KIND_CHOSEN_NOTE;

  protected isChosen(kind: EhrWriteKind): boolean {
    return this.chosen().includes(kind.id);
  }

  /** "always included" only beside a ticked box; while the type is not ticked the box is empty, so say when it is sent. */
  protected includedHint(kind: EhrWriteKind): string {
    return this.isLocked(kind) ? ALWAYS_INCLUDED_NOTE : includedWhenTickedNote(kind.resourceType);
  }

  protected isLocked(kind: EhrWriteKind): boolean {
    return !kind.optional && this.typeSelected();
  }
}
