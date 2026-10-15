import { Component, DestroyRef, ElementRef, HostListener, inject, input, signal, viewChild } from '@angular/core';

let nextId = 0;

/** Length of the slide-out; keep in step with the .is-closing animation duration in page-help.component.scss. */
export const CLOSE_ANIMATION_MS = 200;

function prefersReducedMotion(): boolean {
  return typeof window !== 'undefined' && !!window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
}

/**
 * A page's "how to read this" help, kept off the page itself: an (i) button that sits next to the page title and
 * opens the help in a side drawer. The help content is projected, and is laid out with the shared .info-panel
 * styles (info-lead / info-section / info-heading / info-tag / info-note ...), so existing explainer markup moves
 * over unchanged.
 */
@Component({
  selector: 'app-page-help',
  standalone: true,
  templateUrl: './page-help.component.html',
  styleUrl: './page-help.component.scss',
})
export class PageHelpComponent {
  /** Drawer heading, e.g. "How to read Audit Logs". Not named `title`: that would also stay on the host element as
   *  the native tooltip attribute, showing over the whole drawer. */
  readonly heading = input('How to read this page');

  /** The drawer is rendered while open, and stays rendered through its closing animation. */
  readonly open = signal(false);
  readonly closing = signal(false);
  readonly titleId = `page-help-title-${nextId++}`;

  private readonly trigger = viewChild.required<ElementRef<HTMLButtonElement>>('trigger');
  private readonly closeButton = viewChild<ElementRef<HTMLButtonElement>>('closeButton');
  private closeTimer: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    inject(DestroyRef).onDestroy(() => clearTimeout(this.closeTimer));
  }

  show(): void {
    clearTimeout(this.closeTimer);
    this.closing.set(false);
    this.open.set(true);
    // Move focus into the drawer once it has rendered.
    setTimeout(() => this.closeButton()?.nativeElement.focus());
  }

  hide(): void {
    if (!this.open() || this.closing()) {
      return;
    }
    if (prefersReducedMotion()) {
      this.finishClose();
      return;
    }
    // Keep it rendered while the slide-out plays (matches the .is-closing animation in the stylesheet).
    this.closing.set(true);
    this.closeTimer = setTimeout(() => this.finishClose(), CLOSE_ANIMATION_MS);
  }

  private finishClose(): void {
    this.open.set(false);
    this.closing.set(false);
    this.trigger().nativeElement.focus();
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    this.hide();
  }
}
