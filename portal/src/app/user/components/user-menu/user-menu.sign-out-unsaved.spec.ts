import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { UserMenuComponent } from './user-menu.component';
import { AuthService } from '../../../auth/services/auth.service';
import { UserProfileService } from '../../services/user-profile.service';
import { LayoutService } from '../../../services/layout.service';
import { DensityService } from '../../../services/density.service';
import { UnsavedChangesRegistryService } from '../../../core/services/unsaved-changes-registry.service';
import { UnsavedChangesPromptService } from '../../../core/services/unsaved-changes-prompt.service';

/** Sign out ends the session before it navigates, so the route's leave prompt never saw it and unsaved changes
 *  were dropped without a word. With something unsaved, Sign out now shows the "Leave this page?" modal in place
 *  of its own inline confirm. */
describe('User menu — Sign out with unsaved changes', () => {
  let auth: { logout: jasmine.Spy };
  let prompt: jasmine.SpyObj<UnsavedChangesPromptService>;
  let dirty: boolean;

  function mount() {
    auth = { logout: jasmine.createSpy('logout') };
    prompt = jasmine.createSpyObj<UnsavedChangesPromptService>('UnsavedChangesPromptService', ['confirmDiscard']);
    dirty = false;
    TestBed.configureTestingModule({
      imports: [UserMenuComponent],
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: auth },
        { provide: UserProfileService, useValue: {
            fullName: () => 'Test User', initials: () => 'TU', profile: () => ({ avatarColor: '#000000', email: 'test@example.com', role: 'Admin' }), theme: () => 'light', setTheme: () => undefined } },
        { provide: LayoutService, useValue: { mode: () => 'standard', set: () => undefined } },
        { provide: DensityService, useValue: { mode: () => 'comfortable', set: () => undefined } },
        { provide: UnsavedChangesPromptService, useValue: prompt },
      ],
    });
    TestBed.inject(UnsavedChangesRegistryService).register(() => dirty, { onDestroy: () => () => undefined } as never);
    const fixture = TestBed.createComponent(UserMenuComponent);
    fixture.detectChanges();
    return fixture.componentInstance as unknown as { confirmLogout(): void; showLogout(): boolean };
  }

  it('nothing unsaved: the usual inline "Sign out of Segue?" confirm, no modal', () => {
    const menu = mount();
    menu.confirmLogout();
    expect(menu.showLogout()).toBeTrue();
    expect(prompt.confirmDiscard).not.toHaveBeenCalled();
    expect(auth.logout).not.toHaveBeenCalled();
  });

  it('something unsaved: the "Leave this page?" modal instead — Cancel stays signed in', () => {
    const menu = mount();
    dirty = true;
    prompt.confirmDiscard.and.returnValue(of(false));
    menu.confirmLogout();
    expect(prompt.confirmDiscard).toHaveBeenCalledTimes(1);
    expect(menu.showLogout()).withContext('not the inline confirm as well').toBeFalse();
    expect(auth.logout).not.toHaveBeenCalled();
  });

  it('something unsaved: Leave signs out', () => {
    const menu = mount();
    dirty = true;
    prompt.confirmDiscard.and.returnValue(of(true));
    menu.confirmLogout();
    expect(auth.logout).toHaveBeenCalledTimes(1);
  });
});
