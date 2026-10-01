import {
  Component, signal, inject, input, HostListener, ElementRef,
} from '@angular/core';
import { Router } from '@angular/router';
import { AuthService }        from '../../../auth/services/auth.service';
import { UserProfileService } from '../../services/user-profile.service';
import { AppTheme }           from '../../models/user-profile.model';
import { LayoutService, LayoutMode } from '../../../services/layout.service';
import { DensityService, DensityMode } from '../../../services/density.service';
import { UnsavedChangesRegistryService } from '../../../core/services/unsaved-changes-registry.service';
import { UnsavedChangesPromptService } from '../../../core/services/unsaved-changes-prompt.service';

@Component({
  selector:    'app-user-menu',
  standalone:  true,
  imports:     [],
  templateUrl: './user-menu.component.html',
  styleUrl:    './user-menu.component.scss',
})
export class UserMenuComponent {
  private readonly el      = inject(ElementRef);
  private readonly router  = inject(Router);
  protected readonly auth     = inject(AuthService);
  protected readonly profSvc  = inject(UserProfileService);
  protected readonly layoutSvc = inject(LayoutService);
  protected readonly densitySvc = inject(DensityService);
  private readonly unsavedChanges = inject(UnsavedChangesRegistryService);
  private readonly unsavedPrompt  = inject(UnsavedChangesPromptService);

  // 'topbar' (layout 'standard', default) anchors the panel top-right off the topbar trigger;
  // 'sidebar' (layout 'focused') anchors it bottom-left off a trigger living at the foot of the
  // sidebar instead — see
  // sidebar.component.html, which is the only caller that ever passes 'sidebar'.
  readonly anchor    = input<'topbar' | 'sidebar'>('topbar');
  // Only meaningful with anchor='sidebar': the sidebar's own collapsed state, so the trigger can
  // drop down to an avatar-only chip the same way the nav items above it already do.
  readonly collapsed = input(false);

  protected readonly menuOpen   = signal(false);
  protected readonly showLogout = signal(false);
  protected readonly layout     = this.layoutSvc.mode;
  protected readonly density    = this.densitySvc.mode;

  protected readonly profile  = this.profSvc.profile;
  protected readonly theme    = this.profSvc.theme;
  protected readonly fullName = this.profSvc.fullName;
  protected readonly initials = this.profSvc.initials;

  @HostListener('document:click', ['$event'])
  onDocumentClick(e: MouseEvent): void {
    if (!this.el.nativeElement.contains(e.target as Node)) {
      this.menuOpen.set(false);
      this.showLogout.set(false);
    }
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    this.menuOpen.set(false);
    this.showLogout.set(false);
  }

  toggleMenu(e: MouseEvent): void {
    e.stopPropagation();
    this.menuOpen.update(v => !v);
    this.showLogout.set(false);
  }

  navigate(path: string): void {
    this.menuOpen.set(false);
    this.router.navigate([path]);
  }

  setTheme(theme: AppTheme): void {
    this.profSvc.setTheme(theme);
  }

  setLayout(mode: LayoutMode): void {
    this.layoutSvc.set(mode);
  }

  setDensity(mode: DensityMode): void {
    this.densitySvc.set(mode);
  }

  closeMenu(): void      { this.menuOpen.set(false); this.showLogout.set(false); }
  /** Sign out ends the session BEFORE navigating, so the route's leave prompt never sees it (it waves through
   *  anyone already signed out, so session timeouts are not blocked). With unsaved changes on the page, ask
   *  here instead — the same "Leave this page?" modal, in place of the inline sign-out confirm, not after it. */
  confirmLogout(): void {
    if (!this.unsavedChanges.hasAnyUnsavedChanges()) {
      this.showLogout.set(true);
      return;
    }
    this.menuOpen.set(false);
    this.unsavedPrompt.confirmDiscard().subscribe(leave => {
      if (leave) this.auth.logout();
    });
  }
  cancelLogout(): void   { this.showLogout.set(false); }

  logout(): void {
    this.menuOpen.set(false);
    this.showLogout.set(false);
    this.auth.logout();
  }
}
