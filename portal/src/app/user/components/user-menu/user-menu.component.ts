import {
  Component, signal, inject, HostListener, ElementRef,
} from '@angular/core';
import { Router } from '@angular/router';
import { AuthService }        from '../../../auth/services/auth.service';
import { UserProfileService } from '../../services/user-profile.service';
import { AppTheme }           from '../../models/user-profile.model';

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
  protected readonly auth    = inject(AuthService);
  protected readonly profSvc = inject(UserProfileService);

  protected readonly menuOpen   = signal(false);
  protected readonly showLogout = signal(false);

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

  confirmLogout(): void  { this.showLogout.set(true); }
  cancelLogout(): void   { this.showLogout.set(false); }

  logout(): void {
    this.menuOpen.set(false);
    this.showLogout.set(false);
    this.auth.logout();
  }
}
