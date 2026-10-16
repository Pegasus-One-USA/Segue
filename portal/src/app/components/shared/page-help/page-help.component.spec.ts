import { Component } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { CLOSE_ANIMATION_MS, PageHelpComponent } from './page-help.component';

@Component({
  standalone: true,
  imports: [PageHelpComponent],
  template: `<app-page-help heading="How to read Audit Logs"><p class="projected">Every row is one change.</p></app-page-help>`,
})
class HostComponent {}

describe('PageHelpComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let root: HTMLElement;

  const trigger = () => root.querySelector<HTMLButtonElement>('.help-trigger')!;
  const drawer = () => root.querySelector<HTMLElement>('.help-drawer');

  beforeEach(() => {
    fixture = TestBed.configureTestingModule({ imports: [HostComponent] }).createComponent(HostComponent);
    fixture.detectChanges();
    root = fixture.nativeElement;
  });

  it('shows only the (i) button until it is clicked', () => {
    expect(trigger().getAttribute('aria-label')).toBe('How to read Audit Logs');
    expect(drawer()).toBeNull();
    expect(root.querySelector('app-page-help')!.hasAttribute('title')).toBeFalse();
  });

  it('opens the drawer with the heading and the projected help, and focuses Close', fakeAsync(() => {
    trigger().click();
    fixture.detectChanges();
    tick();

    expect(drawer()).not.toBeNull();
    expect(drawer()!.getAttribute('role')).toBe('dialog');
    expect(root.querySelector('.help-drawer-title')!.textContent).toContain('How to read Audit Logs');
    expect(root.querySelector('.help-drawer .projected')!.textContent).toContain('Every row is one change.');
    expect(document.activeElement).toBe(root.querySelector('.help-drawer-close'));
  }));

  it('closes on the close button, the backdrop and Escape, returning focus to the (i) button', fakeAsync(() => {
    for (const close of [
      () => root.querySelector<HTMLButtonElement>('.help-drawer-close')!.click(),
      () => root.querySelector<HTMLElement>('.help-backdrop')!.click(),
      () => document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' })),
    ]) {
      trigger().click();
      fixture.detectChanges();
      tick();

      close();
      fixture.detectChanges();
      tick(CLOSE_ANIMATION_MS);
      fixture.detectChanges();

      expect(drawer()).toBeNull();
      expect(document.activeElement).toBe(trigger());
    }
  }));

  it('keeps the drawer on screen, sliding out, until the close animation has played', fakeAsync(() => {
    trigger().click();
    fixture.detectChanges();
    tick();

    root.querySelector<HTMLButtonElement>('.help-drawer-close')!.click();
    fixture.detectChanges();

    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
      expect(drawer()).toBeNull();
      return;
    }
    expect(drawer()!.classList).toContain('is-closing');
    expect(root.querySelector('.help-backdrop')!.classList).toContain('is-closing');

    tick(CLOSE_ANIMATION_MS);
    fixture.detectChanges();
    expect(drawer()).toBeNull();
  }));

  it('reopening during the close animation keeps it open', fakeAsync(() => {
    trigger().click();
    fixture.detectChanges();
    tick();
    root.querySelector<HTMLButtonElement>('.help-drawer-close')!.click();
    fixture.detectChanges();

    trigger().click();
    fixture.detectChanges();
    tick(CLOSE_ANIMATION_MS);
    fixture.detectChanges();

    expect(drawer()).not.toBeNull();
    expect(drawer()!.classList).not.toContain('is-closing');
  }));

  it('keeps Tab inside the open drawer', fakeAsync(() => {
    trigger().click();
    fixture.detectChanges();
    tick();

    expect(drawer()!.hasAttribute('cdktrapfocus')).toBeTrue();
  }));

  it('leaves an Escape that something else already handled', fakeAsync(() => {
    trigger().click();
    fixture.detectChanges();
    tick();

    const handledFirst = (e: Event) => e.preventDefault();
    document.addEventListener('keydown', handledFirst, true);
    try {
      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', cancelable: true }));
    } finally {
      document.removeEventListener('keydown', handledFirst, true);
    }
    fixture.detectChanges();
    tick(CLOSE_ANIMATION_MS);

    expect(drawer()).not.toBeNull();
  }));

  it('leaves an Escape that belongs to an overlay such as a dialog or select', fakeAsync(() => {
    trigger().click();
    fixture.detectChanges();
    tick();

    const overlay = document.createElement('div');
    overlay.className = 'cdk-overlay-container';
    const inner = document.createElement('button');
    overlay.appendChild(inner);
    document.body.appendChild(overlay);
    try {
      inner.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
    } finally {
      overlay.remove();
    }
    fixture.detectChanges();
    tick(CLOSE_ANIMATION_MS);

    expect(drawer()).not.toBeNull();
  }));
});
