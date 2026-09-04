import { Component, DestroyRef, effect, inject, input, output, HostListener } from '@angular/core';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'app-modal-overlay',
  standalone: true,
  imports: [],
  templateUrl: './modal-overlay.component.html',
  styleUrl: './modal-overlay.component.scss',
})
export class ModalOverlayComponent {
  readonly open = input(false);
  // Wizards set this false: an accidental backdrop click shouldn't discard an in-progress, multi-step form.
  readonly closeOnBackdropClick = input(true);
  readonly closed = output<void>();

  constructor() {
    // The backdrop is `position: absolute` against .shell-content (see modal-overlay.component.scss),
    // so on its own it doesn't stop that content area from scrolling — without this, a tall page keeps
    // its own scrollbar active (and interactive) right alongside whatever this modal's own content
    // scrolls internally. Some browsers treat <html> (not <body>) as the actual root scroller, so both
    // need locking.
    effect(() => {
      const value = this.open() ? 'hidden' : '';
      document.documentElement.style.overflow = value;
      document.body.style.overflow = value;
    });
    inject(DestroyRef).onDestroy(() => {
      document.documentElement.style.overflow = '';
      document.body.style.overflow = '';
    });
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.open()) this.closed.emit();
  }

  onBackdropClick(event: MouseEvent): void {
    if (!this.closeOnBackdropClick()) return;
    if ((event.target as HTMLElement).classList.contains('modal-backdrop')) {
      this.closed.emit();
    }
  }
}
