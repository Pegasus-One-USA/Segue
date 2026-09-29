import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { of, throwError } from 'rxjs';
import { authInterceptor } from './auth.interceptor';
import { TokenService } from '../services/token.service';
import { AuthService } from '../services/auth.service';
import { TokenRefreshCoordinator } from '../services/token-refresh-coordinator.service';
import { SessionExpiredDialogService } from '../services/session-expired-dialog.service';

describe('authInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let tokenService: jasmine.SpyObj<TokenService>;
  let coordinator: jasmine.SpyObj<TokenRefreshCoordinator>;
  let sessionExpired: jasmine.SpyObj<SessionExpiredDialogService>;

  beforeEach(() => {
    tokenService = jasmine.createSpyObj<TokenService>('TokenService', ['getCsrfToken', 'clearTokens']);
    coordinator = jasmine.createSpyObj<TokenRefreshCoordinator>('TokenRefreshCoordinator', ['refresh']);
    sessionExpired = jasmine.createSpyObj<SessionExpiredDialogService>('SessionExpiredDialogService', ['show']);

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        { provide: TokenService, useValue: tokenService },
        // The interceptor only ever calls TokenRefreshCoordinator.refresh() -- the closures it hands
        // that as startRefresh/onAlreadyRefreshed are never invoked in these specs (the coordinator
        // itself is fully stubbed below), so AuthService itself never needs to do anything real.
        { provide: AuthService, useValue: {} },
        { provide: TokenRefreshCoordinator, useValue: coordinator },
        { provide: SessionExpiredDialogService, useValue: sessionExpired },
      ],
    });

    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('re-reads the CSRF cookie for the retry instead of reusing the header from before the refresh', () => {
    tokenService.getCsrfToken.and.returnValues('token-before-refresh', 'token-after-refresh');
    coordinator.refresh.and.returnValue(of(undefined));

    let result: unknown;
    http.post('/api/thing', { a: 1 }).subscribe(r => (result = r));

    const first = httpMock.expectOne('/api/thing');
    expect(first.request.headers.get('X-CSRF-Token')).toBe('token-before-refresh');
    first.flush(null, { status: 401, statusText: 'Unauthorized' });

    const retry = httpMock.expectOne('/api/thing');
    expect(retry.request.headers.get('X-CSRF-Token'))
      .withContext('retry must carry the CSRF cookie value as it is AFTER the refresh rotated it, not before')
      .toBe('token-after-refresh');
    retry.flush({ ok: true });

    expect(result).toEqual({ ok: true });
    expect(sessionExpired.show).not.toHaveBeenCalled();
  });

  it('does not force a logout when the retried request fails for a reason unrelated to auth (e.g. a validation error)', () => {
    tokenService.getCsrfToken.and.returnValue('token');
    coordinator.refresh.and.returnValue(of(undefined));

    let error: unknown;
    http.put('/api/thing/1', { a: 1 }).subscribe({ error: (e) => (error = e) });

    httpMock.expectOne('/api/thing/1').flush(null, { status: 401, statusText: 'Unauthorized' });
    httpMock.expectOne('/api/thing/1').flush({ message: 'Invalid field' }, { status: 400, statusText: 'Bad Request' });

    expect((error as { status: number }).status).toBe(400);
    expect(sessionExpired.show).not.toHaveBeenCalled();
    expect(tokenService.clearTokens).not.toHaveBeenCalled();
  });

  it('forces a logout when the silent refresh itself fails', () => {
    tokenService.getCsrfToken.and.returnValue('token');
    coordinator.refresh.and.returnValue(throwError(() => new Error('refresh failed')));

    let error: unknown;
    http.delete('/api/thing/1').subscribe({ error: (e) => (error = e) });

    httpMock.expectOne('/api/thing/1').flush(null, { status: 401, statusText: 'Unauthorized' });
    // No retry request is ever made -- the refresh itself failed.
    httpMock.expectNone('/api/thing/1');

    expect(sessionExpired.show).toHaveBeenCalledTimes(1);
    expect(tokenService.clearTokens).toHaveBeenCalledTimes(1);
    expect((error as { status: number }).status).toBe(401);
  });

  it('never attempts a refresh for a 401 from an /auth/ endpoint (e.g. a failed login)', () => {
    let error: unknown;
    http.post('/auth/login', { email: 'a@b.com' }).subscribe({ error: (e) => (error = e) });

    httpMock.expectOne('/auth/login').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(coordinator.refresh).not.toHaveBeenCalled();
    expect(sessionExpired.show).not.toHaveBeenCalled();
    expect((error as { status: number }).status).toBe(401);
  });
});
