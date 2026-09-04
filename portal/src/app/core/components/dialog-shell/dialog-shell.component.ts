import { Component, HostListener, Input, effect, inject, viewChild } from '@angular/core';
import { NgComponentOutlet } from '@angular/common';
import { DialogEntry, DialogService } from '../../services/dialog.service';

/** One instance per open dialog (see DialogOutletComponent) — the actual backdrop/panel/chrome
 *  (universal × close, opt-in maximize) wrapping whatever component DialogService.open() was called
 *  with, projected via NgComponentOutlet.
 *
 *  Close logic itself is NOT duplicated here — every explicit close trigger (× and Esc always;
 *  backdrop-click only when the entry isn't disableClose) just calls entry.dialogRef.attemptClose(),
 *  the single generic dirty-check entry point every dialog's own Cancel button also calls (see
 *  DialogRef.attemptClose in dialog.service.ts for what "dirty" means and how it's detected). This
 *  component's only job re: closing is wiring the ×/Esc/backdrop TRIGGERS and handing this shell's
 *  copy of the rendered component instance to the DialogRef so attemptClose() has something to check
 *  in the first place. */
@Component({
  selector: 'app-dialog-shell',
  standalone: true,
  imports: [NgComponentOutlet],
  templateUrl: './dialog-shell.component.html',
  styleUrl: './dialog-shell.component.scss',
})
export class DialogShellComponent {
  @Input({ required: true }) entry!: DialogEntry;

  private readonly dialogService = inject(DialogService);
  private readonly outlet = viewChild(NgComponentOutlet);

  constructor() {
    // NgComponentOutlet's componentInstance only exists once Angular has actually created the
    // projected component — unavailable at DialogService.open() time, so it's handed to the
    // DialogRef here instead, the first place/time it's reachable.
    effect(() => {
      const instance = this.outlet()?.componentInstance;
      if (instance) this.entry.dialogRef.componentInstance = instance;
    });
  }

  toggleMaximize(): void {
    this.entry.isMaximized.update(v => !v);
  }

  attemptClose(): void {
    this.entry.dialogRef.attemptClose();
  }

  onBackdropClick(event: MouseEvent): void {
    if (this.entry.disableClose) return;
    if ((event.target as HTMLElement).classList.contains('app-dialog-backdrop')) this.attemptClose();
  }

  // Every open dialog gets its own DialogShellComponent instance, each with this same listener — only
  // the topmost one (last in the stack) should actually respond, so a stacked confirm-on-top-of-dialog
  // doesn't close both at once.
  @HostListener('document:keydown.escape')
  onEscape(): void {
    const stack = this.dialogService.stack();
    if (stack[stack.length - 1]?.id !== this.entry.id) return;
    this.attemptClose();
  }
}
