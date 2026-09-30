import { Component, viewChild } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatMenuModule, MatMenuTrigger } from '@angular/material/menu';
import { MatIconModule } from '@angular/material/icon';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

/** The user-detail Status menu was the one mat-menu in the portal without class="app-kebab-menu", so it fell back
 *  to Material's own grey panel while every workflow "⋮" menu is white. This host mirrors that menu's markup and
 *  measures real computed styles (Karma loads src/styles.scss): the same look as the workflow row menu, with
 *  Suspend styled like the workflow menu's Delete. */
@Component({
  standalone: true,
  imports: [MatMenuModule, MatIconModule],
  template: `
    <button #trigger="matMenuTrigger" [matMenuTriggerFor]="statusMenu">Status</button>
    <mat-menu #statusMenu="matMenu" class="app-kebab-menu">
      <button mat-menu-item class="probe-disable"><mat-icon>remove_circle</mat-icon>Disable User</button>
      <button mat-menu-item class="menu-item-danger probe-suspend"><mat-icon>block</mat-icon>Suspend User</button>
    </mat-menu>
  `,
})
class StatusMenuHostComponent {
  readonly trigger = viewChild.required<MatMenuTrigger>('trigger');
}

describe('User detail Status menu styling', () => {
  const RED = 'rgb(239, 68, 68)';
  const ICON_GREY = 'rgb(148, 163, 184)';

  let host: HTMLElement;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [StatusMenuHostComponent], providers: [provideNoopAnimations()] });
    const fixture = TestBed.createComponent(StatusMenuHostComponent);
    fixture.detectChanges();
    fixture.componentInstance.trigger().openMenu();
    fixture.detectChanges();
    host = document.querySelector<HTMLElement>('.probe-suspend')!.closest<HTMLElement>('.mat-mdc-menu-panel')!;
  });

  it('uses the shared white, compact workflow-menu panel', () => {
    expect(getComputedStyle(host).backgroundColor).toBe('rgb(255, 255, 255)');
    expect(document.querySelector<HTMLElement>('.probe-suspend')!.getBoundingClientRect().height).toBe(38);
  });

  it('shows Suspend User in red, text and icon, like the workflow menu Delete', () => {
    const item = document.querySelector<HTMLElement>('.probe-suspend')!;
    expect(getComputedStyle(item).color).toBe(RED);
    expect(getComputedStyle(item.querySelector('mat-icon')!).color).toBe(RED);
  });

  it('keeps a normal action icon grey', () => {
    // Blur whatever the menu auto-focused on open so the resting colour is measured, not the hover/focus one.
    (document.activeElement as HTMLElement | null)?.blur();
    const item = document.querySelector<HTMLElement>('.probe-disable')!;
    item.classList.remove('cdk-focused', 'cdk-keyboard-focused', 'cdk-program-focused');
    expect(getComputedStyle(item.querySelector('mat-icon')!).color).toBe(ICON_GREY);
  });
});
