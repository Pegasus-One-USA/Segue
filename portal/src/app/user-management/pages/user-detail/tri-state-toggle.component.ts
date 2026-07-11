// user-management/pages/user-detail/tri-state-toggle.component.ts
import { Component, Input, Output, EventEmitter, ChangeDetectionStrategy } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

// 'inherit' = no direct override (use whatever the role grants); 'grant'/'deny' = explicit override.
export type TriState = 'deny' | 'inherit' | 'grant';

const CYCLE: TriState[] = ['inherit', 'grant', 'deny'];

// A single checkbox-sized square, like the plain checkbox on the role-permissions grid, except it
// cycles through three states instead of two: Inherit (dash) -> Grant (check) -> Deny (cross) -> ...
// Hand-rolled instead of MatButtonToggleGroup — button-toggle always renders one button per option
// side by side, which reads as "3 buttons" rather than a single control, and its structural styles
// are global/ViewEncapsulation.None, making it hard to reliably shrink into a dense grid cell anyway.
@Component({
  selector: 'app-tri-state-toggle',
  standalone: true,
  imports: [MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './tri-state-toggle.component.html',
  styleUrls: ['./tri-state-toggle.component.scss'],
})
export class TriStateToggleComponent {
  // null renders as the "inherit" look but is otherwise treated like 'inherit' — used for a
  // row-level control whose permissions don't all currently share one state (mixed).
  @Input() value: TriState | null = 'inherit';
  @Input() disabled = false;
  @Input() tooltip = '';
  @Input() ariaLabel = 'Permission override';
  @Output() valueChange = new EventEmitter<TriState>();

  get icon(): string {
    switch (this.value) {
      case 'grant': return 'check';
      case 'deny':  return 'close';
      default:      return 'remove';
    }
  }

  get stateClass(): string {
    switch (this.value) {
      case 'grant': return 'tri-toggle--grant';
      case 'deny':  return 'tri-toggle--deny';
      default:      return 'tri-toggle--inherit';
    }
  }

  onClick(): void {
    if (this.disabled) return;
    const currentIndex = this.value === null ? -1 : CYCLE.indexOf(this.value);
    const next = CYCLE[(currentIndex + 1) % CYCLE.length];
    this.valueChange.emit(next);
  }
}
