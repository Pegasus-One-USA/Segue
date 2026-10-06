import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Observable, of, throwError } from 'rxjs';
import { BrandingSettingsComponent } from './branding-settings.component';
import { unsavedChangesGuard } from '../../../core/guards/unsaved-changes.guard';
import { BrandingService } from '../../../services/branding.service';
import { ThemeService } from '../../../services/theme.service';
import { ToastService } from '../../../services/toast.service';
import { AuthStore } from '../../../auth/store/auth.store';
import { PermissionService } from '../../../auth/services/permission.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { BrandConfiguration, DEFAULT_BRANDING } from '../../../models/brand-configuration.model';

/** Branding through the real router and the real unsavedChangesGuard / UnsavedChangesPromptService — every sidebar
 *  and settings menu link is a router navigation, so this is the path each of them takes. Only the modal itself is
 *  faked, answering Save / Leave / Cancel. */
@Component({ standalone: true, template: 'elsewhere' })
class OtherPage {}

describe('Branding — unsaved changes warning on navigation', () => {
  let harness: RouterTestingHarness;
  let router: Router;
  let dialog: jasmine.SpyObj<MatDialog>;
  let save: jasmine.Spy<(config: BrandConfiguration) => Observable<BrandConfiguration>>;
  let page: HTMLElement;

  const BRANDING_URL = '/settings/branding';

  beforeEach(async () => {
    const current = signal<BrandConfiguration>({ ...DEFAULT_BRANDING });
    save = jasmine.createSpy('save').and.callFake((config: BrandConfiguration) => {
      current.set(config);
      return of(config);
    });
    dialog = jasmine.createSpyObj<MatDialog>('MatDialog', ['open']);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'settings/branding', component: BrandingSettingsComponent, canDeactivate: [unsavedChangesGuard] },
          { path: 'dashboard', component: OtherPage },
          { path: 'workflows', component: OtherPage },
          { path: 'execution-history', component: OtherPage },
        ]),
        { provide: MatDialog, useValue: dialog },
        { provide: BrandingService, useValue: {
            current, save, applyToDocument: (config: BrandConfiguration) => current.set(config),
            resetToDefault: () => current.set({ ...DEFAULT_BRANDING }) } },
        { provide: ThemeService, useValue: { mode: signal('light'), resolved: () => 'light', set: () => undefined } },
        { provide: ToastService, useValue: jasmine.createSpyObj('ToastService', ['success', 'error', 'warning']) },
        { provide: AuthStore, useValue: { isAuthenticated: () => true } },
        { provide: PermissionService, useValue: { hasPermission: () => true } },
        { provide: PermissionActionGuard, useValue: { ensure: () => true } },
      ],
    });
    harness = await RouterTestingHarness.create();
    router = TestBed.inject(Router);
    await harness.navigateByUrl(BRANDING_URL, BrandingSettingsComponent);
    page = harness.routeNativeElement!;
  });

  /** Let the 120ms live-preview debounce and the branding effect run, as they would between edit and menu click. */
  async function settle(): Promise<void> {
    await new Promise(resolve => setTimeout(resolve, 200));
    harness.fixture.detectChanges();
  }

  function answer(choice: boolean | 'save'): void {
    dialog.open.and.returnValue({ afterClosed: () => of(choice) } as never);
  }

  async function clickMenu(url: string): Promise<void> {
    await router.navigateByUrl(url);
    harness.fixture.detectChanges();
  }

  function type(id: string, value: string): void {
    const input = page.querySelector<HTMLInputElement>(`#${id}`)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  function pickSwatch(index: number, value: string): void {
    const swatch = page.querySelectorAll<HTMLInputElement>('input[type="color"]')[index];
    swatch.value = value;
    swatch.dispatchEvent(new Event('input'));
  }

  function clickButton(label: string): void {
    Array.from(page.querySelectorAll<HTMLButtonElement>('button')).find(b => b.textContent!.trim() === label)!.click();
  }

  const companyName = () => page.querySelector<HTMLInputElement>('#bs-companyName')!.value;

  it('1. no changes: a menu click navigates without the popup', async () => {
    await clickMenu('/dashboard');

    expect(dialog.open).not.toHaveBeenCalled();
    expect(router.url).toBe('/dashboard');
  });

  it('2+3. modified: the popup appears; Cancel stays on Branding with the edit intact', async () => {
    type('bs-companyName', 'Acme Health');
    await settle();
    answer(false);

    await clickMenu('/workflows');

    expect(dialog.open).toHaveBeenCalledTimes(1);
    expect((dialog.open.calls.mostRecent().args[1] as { data: { saveLabel?: string } }).data.saveLabel).toBe('Save');
    expect(router.url).toBe(BRANDING_URL);
    expect(companyName()).toBe('Acme Health');
  });

  it('4. Leave: navigates without saving', async () => {
    type('bs-companyName', 'Acme Health');
    await settle();
    answer(true);

    await clickMenu('/dashboard');

    expect(save).not.toHaveBeenCalled();
    expect(router.url).toBe('/dashboard');
  });

  it('5. Save: saves the edit, then navigates', async () => {
    type('bs-companyName', 'Acme Health');
    type('bs-supportEmail', 'help@acme.example');
    await settle();
    answer('save');

    await clickMenu('/execution-history');

    expect(save).toHaveBeenCalledTimes(1);
    expect(save.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({
      companyName: 'Acme Health', supportEmail: 'help@acme.example' }));
    expect(router.url).toBe('/execution-history');
  });

  it('5b. Save that fails: stays on Branding with the edit intact', async () => {
    save.and.returnValue(throwError(() => ({ error: { message: 'nope' } })));
    type('bs-companyName', 'Acme Health');
    await settle();
    answer('save');

    await clickMenu('/dashboard');

    expect(router.url).toBe(BRANDING_URL);
    expect(companyName()).toBe('Acme Health');
  });

  it('6. saved with the page\'s own Save button: the next menu click has no popup', async () => {
    type('bs-companyName', 'Acme Health');
    await settle();
    clickButton('Save');
    await settle();

    await clickMenu('/workflows');

    expect(save).toHaveBeenCalledTimes(1);
    expect(dialog.open).not.toHaveBeenCalled();
    expect(router.url).toBe('/workflows');
  });

  it('7. modified then put back to the original value: treated as unchanged, no popup', async () => {
    const original = companyName();
    type('bs-companyName', 'Acme Health');
    await settle();
    type('bs-companyName', original);
    await settle();

    await clickMenu('/dashboard');

    expect(dialog.open).not.toHaveBeenCalled();
    expect(router.url).toBe('/dashboard');
  });

  // 9. Fields that write through setValue() (never "dirty") and live-preview straight into BrandingService — the
  //    two reasons the warning never fired before.
  for (const [label, edit] of [
    ['a colour swatch', () => pickSwatch(0, '#123456')],
    ['the theme mode pills', () => clickButton('Dark')],
    ['the loader style pills', () => clickButton('Spinner')],
    ['a text field (footer)', () => type('bs-footerText', 'Acme footer')],
    ['the font select', () => {
      const select = page.querySelector<HTMLSelectElement>('#bs-fontFamily')!;
      select.selectedIndex = select.options.length - 1;
      select.dispatchEvent(new Event('change'));
    }],
  ] as const) {
    it(`9. editing ${label} triggers the popup, even after the live preview has applied it`, async () => {
      edit();
      await settle();
      answer(false);

      await clickMenu('/dashboard');

      expect(dialog.open).toHaveBeenCalledTimes(1);
      expect(router.url).toBe(BRANDING_URL);
    });
  }
});
