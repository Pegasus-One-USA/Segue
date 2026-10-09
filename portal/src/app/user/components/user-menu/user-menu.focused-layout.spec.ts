import { TestBed } from '@angular/core/testing';
import { ChildrenOutletContexts, provideRouter } from '@angular/router';
import { UserMenuComponent } from './user-menu.component';
import { AuthService } from '../../../auth/services/auth.service';
import { AuthStore } from '../../../auth/store/auth.store';
import { UserProfileService } from '../../services/user-profile.service';
import { LayoutService } from '../../../services/layout.service';
import { DensityService } from '../../../services/density.service';
import { ToastService } from '../../../services/toast.service';

/** Layout → Focused renders this menu with anchor="sidebar" (sidebar.component.html, the only caller that does). There
 *  it shows the first name only, and the panel header shows the user's whole email instead of an ellipsis. Layout →
 *  Standard (anchor="topbar") keeps the full name and its single-line email exactly as before. */
describe('User menu — Focused layout name and email', () => {
  const SHORT_EMAIL = 'amit@pegasusone.com';
  const LONG_EMAIL = 'firstname.middlename.lastname.integration-testing@a-very-long-subdomain.example-health.org';

  function mount(anchor: 'topbar' | 'sidebar', email: string, firstName = 'Amit') {
    TestBed.configureTestingModule({
      imports: [UserMenuComponent],
      providers: [
        provideRouter([]),
        { provide: ChildrenOutletContexts, useValue: { getContext: () => null } },
        { provide: AuthService, useValue: { logout: () => undefined } },
        { provide: AuthStore, useValue: { isAuthenticated: () => true } },
        { provide: UserProfileService, useValue: {
            fullName: () => [firstName, 'Sable'].filter(Boolean).join(' '), initials: () => 'AS', theme: () => 'light',
            profile: () => ({ firstName, lastName: 'Sable', avatarColor: 'var(--color-primary)', email, roleLabel: 'Super Admin' }) } },
        { provide: LayoutService, useValue: { mode: () => (anchor === 'sidebar' ? 'focused' : 'standard'), set: () => undefined } },
        { provide: DensityService, useValue: { mode: () => 'normal', set: () => undefined } },
        { provide: ToastService, useValue: jasmine.createSpyObj('ToastService', ['warning']) },
      ],
    });
    const fixture = TestBed.createComponent(UserMenuComponent);
    fixture.componentRef.setInput('anchor', anchor);
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('.um-trigger') as HTMLButtonElement).click();
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const text = (selector: string) => root.querySelector(selector)!.textContent!.trim();
    return { root, fixture, text, email: root.querySelector('.um-header-email') as HTMLElement };
  }

  afterEach(() => document.querySelectorAll('.um-panel').forEach(panel => panel.remove()));

  it('Focused: trigger and panel header show the first name only', () => {
    const { text } = mount('sidebar', SHORT_EMAIL);

    expect(text('.um-trigger-name')).toBe('Amit');
    expect(text('.um-header-name')).toBe('Amit');
  });

  it('Focused: a profile with no first name still gets a label (the full name), never a blank', () => {
    const { text } = mount('sidebar', SHORT_EMAIL, '');

    expect(text('.um-trigger-name')).toBe('Sable');
  });

  for (const [label, address] of [['short', SHORT_EMAIL], ['long', LONG_EMAIL]] as const) {
    it(`Focused: a ${label} email is shown in full, not clipped`, () => {
      const { email } = mount('sidebar', address);

      expect(email.textContent!.trim()).toBe(address);
      expect(getComputedStyle(email).textOverflow).not.toBe('ellipsis');
      expect(email.scrollWidth).withContext('content wider than its box = clipped').toBeLessThanOrEqual(email.clientWidth);
    });
  }

  it('Focused: the panel stays inside a phone-width viewport', () => {
    const { root } = mount('sidebar', LONG_EMAIL);

    expect(getComputedStyle(root.querySelector('.um-panel')!).maxWidth).toBe(`${window.innerWidth - 32}px`);
  });

  it('Standard: unchanged — full name, single-line email with ellipsis', () => {
    const { text, email } = mount('topbar', LONG_EMAIL);

    expect(text('.um-trigger-name')).toBe('Amit Sable');
    expect(text('.um-header-name')).toBe('Amit Sable');
    expect(getComputedStyle(email).whiteSpace).toBe('nowrap');
    expect(getComputedStyle(email).textOverflow).toBe('ellipsis');
  });
});
