import { Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { UserListComponent } from './user-list.component';
import { AuthService } from '../../../auth/services/auth.service';
import { IUserService } from '../../../auth/services/i-user.service';
import { ApiUserService } from '../../../auth/services/api-user.service';
import { LoadingService } from '../../../services/loading.service';
import { SKIP_LOADER, loadingInterceptor } from '../../../core/loading.interceptor';
import { USERS_ENDPOINTS } from '../../../core/api-endpoints';

/**
 * Typing in the User Management search lost focus after every keystroke. Each debounced keystroke re-fetches the
 * user list, and that request went through the app-wide loader; AppComponent marks the routed content [inert]
 * while the loader is up, and Chrome blurs a focused element inside an inert subtree the next time it renders.
 * The host below reproduces that [inert] wrapper with the real loading interceptor, and the tests use real timers
 * and real animation frames (not fakeAsync) so that blur can actually happen.
 */
@Component({
  standalone: true,
  imports: [UserListComponent],
  template: `<div [attr.inert]="loading.isLoading() ? '' : null"><app-user-list /></div>`,
})
class InertWhileLoadingHost {
  readonly loading = inject(LoadingService);
}

describe('User Management — search keeps focus while typing', () => {
  const dto = (id: string, email: string, displayName: string) => ({
    id, externalUserId: id, email, displayName, status: 'Active', isEnabled: true, isLocalLoginEnabled: true,
    mustChangePassword: false, globalRoleNames: ['Admin'], createdOnUtc: '2026-10-01T00:00:00Z', lastLoginOnUtc: null,
    mfaEnabled: false, mustSetupMfa: false,
  });
  const USERS = [dto('1', 'amit.sable@example.com', 'Amit Sable'), dto('2', 'jane.doe@example.com', 'Jane Doe')];

  let http: HttpTestingController;
  let input: HTMLInputElement;
  let fixture: ReturnType<typeof TestBed.createComponent<InertWhileLoadingHost>>;

  const sleep = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));
  const frames = () => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
  const userListRequests = (): TestRequest[] => http.match(r => r.url === USERS_ENDPOINTS.list);

  function typeInto(value: string): void {
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  /** Lets the 300ms debounce fire, renders while the request is in flight (when the old code went [inert]), checks
   *  focus, then answers the request. */
  async function searchRoundTrip(): Promise<TestRequest[]> {
    await sleep(350);
    fixture.detectChanges();
    const requests = userListRequests();
    await frames();
    expect(document.activeElement).withContext('focus while the search request is in flight').toBe(input);
    requests.forEach(r => r.flush(USERS));
    fixture.detectChanges();
    return requests;
  }

  const visibleEmails = () =>
    Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('tbody tr'))
      .map(row => row.textContent ?? '')
      .filter(text => text.includes('@'));

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [InertWhileLoadingHost],
      providers: [
        provideHttpClient(withInterceptors([loadingInterceptor])),
        provideHttpClientTesting(),
        provideRouter([]),
        provideNoopAnimations(),
        { provide: IUserService, useClass: ApiUserService },
        { provide: AuthService, useValue: { isAdmin: () => true, hasPermission: () => true, currentUser: () => null } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(InertWhileLoadingHost);
    document.body.appendChild(fixture.nativeElement);
    fixture.detectChanges();
    http.match(r => r.url !== USERS_ENDPOINTS.list).forEach(r => r.flush([]));
    userListRequests().forEach(r => r.flush(USERS));
    fixture.detectChanges();
    await frames();
    input = (fixture.nativeElement as HTMLElement).querySelector('input[type="search"]')!;
    input.focus();
  });

  afterEach(() => (fixture.nativeElement as HTMLElement).remove());

  it('typing "A", "Am", "Ami", "Amit" keeps focus on every keystroke, and the results still filter', async () => {
    for (const value of ['A', 'Am', 'Ami', 'Amit']) {
      typeInto(value);
      await searchRoundTrip();
      await frames();
      expect(document.activeElement).withContext(`focus after "${value}"`).toBe(input);
    }

    expect(visibleEmails().length).toBe(1);
    expect(visibleEmails()[0]).toContain('amit.sable@example.com');
  });

  it('the search request skips the app-wide loader (so the page never goes inert)', async () => {
    typeInto('Amit');

    const requests = await searchRoundTrip();

    expect(requests.length).toBe(1);
    expect(requests[0].request.context.get(SKIP_LOADER)).toBeTrue();
  });

  it('backspacing down to empty (clearing) keeps focus and brings every user back', async () => {
    typeInto('Amit');
    await searchRoundTrip();

    for (const value of ['Ami', 'Am', 'A', '']) {
      typeInto(value);
      await searchRoundTrip();
      expect(document.activeElement).withContext(`focus after backspace to "${value}"`).toBe(input);
    }

    expect(visibleEmails().length).toBe(2);
  });

  it('pasting a whole term keeps focus and filters', async () => {
    typeInto('jane.doe@example.com');
    await searchRoundTrip();

    expect(document.activeElement).toBe(input);
    expect(visibleEmails().length).toBe(1);
  });

  it('opening the page (not a search) still uses the app-wide loader, as before', () => {
    const list = fixture.debugElement.children[0].children[0].componentInstance as UserListComponent;

    list.loadUsers();
    const [request] = userListRequests();

    expect(request.request.context.get(SKIP_LOADER)).toBeFalse();
    request.flush(USERS);
  });
});
