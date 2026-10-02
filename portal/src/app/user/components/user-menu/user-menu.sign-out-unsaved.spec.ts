import { TestBed } from '@angular/core/testing';
import { ChildrenOutletContexts, provideRouter } from '@angular/router';
import { MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';
import { UserMenuComponent } from './user-menu.component';
import { AuthService } from '../../../auth/services/auth.service';
import { AuthStore } from '../../../auth/store/auth.store';
import { UserProfileService } from '../../services/user-profile.service';
import { LayoutService } from '../../../services/layout.service';
import { DensityService } from '../../../services/density.service';
import { ToastService } from '../../../services/toast.service';
import { UnsavedChangesRegistryService } from '../../../core/services/unsaved-changes-registry.service';
import { UnsavedChangesPromptService } from '../../../core/services/unsaved-changes-prompt.service';
import { HasUnsavedChanges } from '../../../core/guards/has-unsaved-changes';

/** Sign out ends the session before it navigates, so the route's leave check never saw it and unsaved changes
 *  were dropped without a word. Sign out now runs that same check against the page on screen: the page's own
 *  state (pages that rely only on the route guard included), anything registered on it, and an in-flight save. */
describe('User menu — Sign out with unsaved changes', () => {
  let auth: { logout: jasmine.Spy };
  let dirtyRegistered: boolean;
  let page: HasUnsavedChanges;

  /** The router's outlet tree, reduced to one active page. */
  const outletsShowing = () => ({
    getContext: () => ({ outlet: { isActivated: true, component: page }, children: { getContext: () => null } }),
  });

  function mount(prompt?: Partial<UnsavedChangesPromptService>) {
    auth = { logout: jasmine.createSpy('logout') };
    dirtyRegistered = false;
    page = { hasUnsavedChanges: () => false };
    TestBed.configureTestingModule({
      imports: [UserMenuComponent],
      providers: [
        provideRouter([]),
        { provide: ChildrenOutletContexts, useFactory: outletsShowing },
        { provide: AuthService, useValue: auth },
        { provide: AuthStore, useValue: { isAuthenticated: () => true } },
        { provide: UserProfileService, useValue: {
            fullName: () => 'Test User', initials: () => 'TU', theme: () => 'light', setTheme: () => undefined,
            profile: () => ({ avatarColor: 'var(--color-primary)', email: 'test@example.com', role: 'Admin' }) } },
        { provide: LayoutService, useValue: { mode: () => 'standard', set: () => undefined } },
        { provide: DensityService, useValue: { mode: () => 'comfortable', set: () => undefined } },
        { provide: ToastService, useValue: jasmine.createSpyObj('ToastService', ['warning']) },
        ...(prompt ? [{ provide: UnsavedChangesPromptService, useValue: prompt }] : []),
      ],
    });
    TestBed.inject(UnsavedChangesRegistryService).register(() => dirtyRegistered, { onDestroy: () => () => undefined } as never);
    const fixture = TestBed.createComponent(UserMenuComponent);
    fixture.detectChanges();
    return fixture.componentInstance as unknown as { confirmLogout(): void; showLogout(): boolean };
  }

  it('nothing unsaved: the usual inline "Sign out of Segue?" confirm, no prompt', () => {
    const confirmLeave = jasmine.createSpy('confirmLeave');
    const menu = mount({ confirmLeave });
    menu.confirmLogout();
    expect(menu.showLogout()).toBeTrue();
    expect(confirmLeave).not.toHaveBeenCalled();
    expect(auth.logout).not.toHaveBeenCalled();
  });

  it('the page itself is dirty (a page that only uses the route guard): its leave check, Cancel stays signed in', () => {
    const confirmLeave = jasmine.createSpy('confirmLeave').and.returnValue(of(false));
    const menu = mount({ confirmLeave });
    page = { hasUnsavedChanges: () => true };
    menu.confirmLogout();
    expect(confirmLeave).toHaveBeenCalledOnceWith(page);
    expect(menu.showLogout()).withContext('not the inline confirm as well').toBeFalse();
    expect(auth.logout).not.toHaveBeenCalled();
  });

  it('only something registered on the page is dirty (e.g. the mapping popover): Leave signs out', () => {
    const confirmLeave = jasmine.createSpy('confirmLeave').and.returnValue(of(true));
    const menu = mount({ confirmLeave });
    dirtyRegistered = true;
    menu.confirmLogout();
    expect(confirmLeave).toHaveBeenCalledTimes(1);
    expect(auth.logout).toHaveBeenCalledTimes(1);
  });

  it('a save still in flight: "wait for the current save to finish", not the unsaved-changes prompt', () => {
    const menu = mount();   // the real prompt service
    const dialog = TestBed.inject(MatDialog);
    const open = spyOn(dialog, 'open');
    page = { hasUnsavedChanges: () => false, isSaveInProgress: () => true };
    menu.confirmLogout();
    expect(TestBed.inject(ToastService).warning)
      .toHaveBeenCalledWith('Please wait for the current save to finish before leaving this page.');
    expect(open).not.toHaveBeenCalled();
    expect(auth.logout).not.toHaveBeenCalled();
  });
});
