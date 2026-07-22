import { Component, input, output, HostListener } from '@angular/core';
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
