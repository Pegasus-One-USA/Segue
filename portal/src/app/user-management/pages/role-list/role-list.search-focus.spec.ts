import { Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { RoleListComponent } from './role-list.component';
import { AuthService } from '../../../auth/services/auth.service';
import { IRoleService } from '../../services/i-role.service';
import { ApiRoleService } from '../../services/api-role.service';
import { LoadingService } from '../../../services/loading.service';
import { SKIP_LOADER, loadingInterceptor } from '../../../core/loading.interceptor';
import { ROLES_ENDPOINTS } from '../../../core/api-endpoints';

/**
 * Same bug and same proof as user-list.search-focus.spec.ts: each debounced search keystroke re-fetched the paged
 * roles through the app-wide loader, AppComponent marked the routed content [inert] while it was up, and Chrome
 * blurred the search box on its next render. Real timers and real frames, so the blur can actually happen.
 */
@Component({
  standalone: true,
  imports: [RoleListComponent],
  template: `<div [attr.inert]="loading.isLoading() ? '' : null"><app-role-list /></div>`,
})
class InertWhileLoadingHost {
  readonly loading = inject(LoadingService);
}

describe('Role Management — search keeps focus while typing', () => {
  const role = (id: string, name: string) => ({
    id, name, description: `${name} role`, permissions: [], isSystemRole: false, isFullAccess: false,
    createdOnUtc: '2026-10-01T00:00:00Z',
  });
  const ALL = [role('1', 'Auditor'), role('2', 'Analyst'), role('3', 'Operator')];
  /** Server-side search, as /roles/paged does it. */
  const page = (search: string | null) => {
    const items = ALL.filter(r => !search || r.name.toLowerCase().includes(search.toLowerCase()));
    return { items, totalCount: items.length, page: 1, pageSize: 10 };
  };

  let http: HttpTestingController;
  let input: HTMLInputElement;
  let fixture: ReturnType<typeof TestBed.createComponent<InertWhileLoadingHost>>;

  const sleep = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));
  const frames = () => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
  const rolePageRequests = (): TestRequest[] => http.match(r => r.url === ROLES_ENDPOINTS.paged);
  const answer = (requests: TestRequest[]) => requests.forEach(r => r.flush(page(r.request.params.get('search'))));

  function typeInto(value: string): void {
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  async function searchRoundTrip(): Promise<TestRequest[]> {
    await sleep(350);
    fixture.detectChanges();
    const requests = rolePageRequests();
    await frames();
    expect(document.activeElement).withContext('focus while the search request is in flight').toBe(input);
    answer(requests);
    fixture.detectChanges();
    return requests;
  }

  const visibleRoles = () =>
    Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('tr.data-row')).map(row => row.textContent ?? '');

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [InertWhileLoadingHost],
      providers: [
        provideHttpClient(withInterceptors([loadingInterceptor])),
        provideHttpClientTesting(),
        provideRouter([]),
        provideNoopAnimations(),
        { provide: IRoleService, useClass: ApiRoleService },
        { provide: AuthService, useValue: { isAdmin: () => true, hasPermission: () => true, currentUser: () => null } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(InertWhileLoadingHost);
    document.body.appendChild(fixture.nativeElement);
    fixture.detectChanges();
    answer(rolePageRequests());
    http.match(() => true).forEach(r => r.flush([]));
    fixture.detectChanges();
    await frames();
    input = (fixture.nativeElement as HTMLElement).querySelector('input[type="search"]')!;
    input.focus();
  });

  afterEach(() => (fixture.nativeElement as HTMLElement).remove());

  it('typing "A", "Au", "Aud" keeps focus on every keystroke, and the results still filter', async () => {
    for (const value of ['A', 'Au', 'Aud']) {
      typeInto(value);
      await searchRoundTrip();
      await frames();
      expect(document.activeElement).withContext(`focus after "${value}"`).toBe(input);
    }

    expect(visibleRoles().length).toBe(1);
    expect(visibleRoles()[0]).toContain('Auditor');
  });

  it('the search request skips the app-wide loader and sends the term to the server', async () => {
    typeInto('Aud');

    const [request] = await searchRoundTrip();

    expect(request.request.context.get(SKIP_LOADER)).toBeTrue();
    expect(request.request.params.get('search')).toBe('Aud');
  });

  it('backspacing down to empty (clearing) keeps focus and brings every role back', async () => {
    typeInto('Aud');
    await searchRoundTrip();

    for (const value of ['Au', 'A', '']) {
      typeInto(value);
      await searchRoundTrip();
      expect(document.activeElement).withContext(`focus after backspace to "${value}"`).toBe(input);
    }

    expect(visibleRoles().length).toBe(3);
  });

  it('opening the page (not a search) still uses the app-wide loader, as before', () => {
    const list = fixture.debugElement.children[0].children[0].componentInstance as RoleListComponent;

    list.loadRoles();
    const [request] = rolePageRequests();

    expect(request.request.context.get(SKIP_LOADER)).toBeFalse();
    answer([request]);
  });
});
