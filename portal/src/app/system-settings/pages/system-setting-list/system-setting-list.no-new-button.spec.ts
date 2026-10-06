import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { NEVER, of } from 'rxjs';
import { SystemSettingListComponent } from './system-setting-list.component';
import { ISystemSettingsService } from '../../services/i-system-settings.service';
import { HapiTerminologyConfigurationService } from '../../services/hapi-terminology-configuration.service';
import { TerminologyStatusHubService } from '../../services/terminology-status-hub.service';
import { AuthStore } from '../../../auth/store/auth.store';
import { IRoleService } from '../../../user-management/services/i-role.service';
import { makeTestUser } from '../../../auth/testing/auth-test-helpers';

/** System Settings no longer offers "New" (creating a raw SystemSetting row). Everything else on the page stays:
 *  the search box, the Decrypt tool, the Terminology Server's Scan for updates, and each row's ⋮ actions. */
describe('System Settings — no "New" button', () => {
  let root: HTMLElement;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [SystemSettingListComponent],
      providers: [
        AuthStore,
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        { provide: IRoleService, useValue: { getRoles: () => of([]) } },
        { provide: ISystemSettingsService, useValue: {
            getAll: () => of([{ key: 'Caching:TerminologyTtlMinutes', value: '60', description: 'Terminology cache TTL.' }]) } },
        { provide: HapiTerminologyConfigurationService, useValue: { getAll: () => of([]) } },
        { provide: TerminologyStatusHubService, useValue: { ensureConnected: () => undefined, statusChanged$: NEVER, reconnected$: NEVER } },
      ],
    });
    // SuperAdmin: the role that used to see "New" (canViewSettingGroups) and every other action on the page.
    TestBed.inject(AuthStore).setUser(makeTestUser([], 'SuperAdmin'));
    const fixture = TestBed.createComponent(SystemSettingListComponent);
    fixture.detectChanges();
    root = fixture.nativeElement as HTMLElement;
  });

  const buttonLabels = () => Array.from(root.querySelectorAll('button')).map(b => b.textContent!.trim());

  it('does not render a "New" button', () => {
    expect(buttonLabels()).not.toContain('New');
  });

  it('keeps the page\'s other actions: search, Decrypt, Scan for updates, and the row menus', () => {
    expect(root.querySelector('input[placeholder="Search by key or description…"]')).toBeTruthy();
    expect(buttonLabels()).toContain('Decrypt');
    expect(buttonLabels().some(label => label.includes('Scan for updates'))).toBeTrue();
    expect(root.querySelectorAll('[aria-haspopup="menu"], .mat-mdc-menu-trigger').length).toBeGreaterThan(0);
  });
});
