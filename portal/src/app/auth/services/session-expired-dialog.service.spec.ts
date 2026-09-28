import { TestBed } from '@angular/core/testing';
import { NavigationEnd, Router } from '@angular/router';
import { of, Subject } from 'rxjs';
import Swal from 'sweetalert2';
import { SessionExpiredDialogService } from './session-expired-dialog.service';
import { IAuthService } from './i-auth.service';
import { TokenService } from './token.service';
import { AuthStore } from '../store/auth.store';
import { ToastService } from '../../services/toast.service';

// Swal.fire renders a real DOM overlay and only resolves on a button click, which nothing in these
// specs simulates — spying it out and returning a promise that never settles is what lets the
// suppression check run to completion without ever reaching logOutAndRedirect().
function stubSwalFire(): jasmine.Spy {
  return spyOn(Swal, 'fire').and.returnValue(new Promise<never>(() => { /* never resolves */ }) as never);
}

describe('SessionExpiredDialogService', () => {
  let service: SessionExpiredDialogService;
  let routerUrl: string;
  let routerEvents: Subject<NavigationEnd>;

  beforeEach(() => {
    routerUrl = '/workflows';
    routerEvents = new Subject<NavigationEnd>();

    TestBed.configureTestingModule({
      providers: [
        SessionExpiredDialogService,
        AuthStore,
        ToastService,
        TokenService,
        { provide: IAuthService, useValue: { logout: () => of(undefined) } },
        // Router.url is a real getter on the class; stubbing the whole service with a plain object
        // that exposes it as a plain property is simpler than instantiating Angular's router here,
        // and this class only ever reads router.url (never routes) plus subscribes to router.events.
        {
          provide: Router,
          useValue: {
            get url() { return routerUrl; },
            events: routerEvents.asObservable(),
            navigate: () => Promise.resolve(true),
          },
        },
      ],
    });
    service = TestBed.inject(SessionExpiredDialogService);
  });

  it('does not show when already on the login page (a stray 401 with nothing to expire)', () => {
    routerUrl = '/auth/login';
    const fire = stubSwalFire();

    service.show();

    expect(fire).not.toHaveBeenCalled();
  });

  it('ignores query/fragment when comparing the current URL to the login page', () => {
    routerUrl = '/auth/login?returnUrl=%2Fworkflows';
    const fire = stubSwalFire();

    service.show();

    expect(fire).not.toHaveBeenCalled();
  });

  it('shows on any page other than the login page', () => {
    routerUrl = '/workflows/123';
    const fire = stubSwalFire();

    service.show();

    expect(fire).toHaveBeenCalledTimes(1);
  });

  it('shows on the login page when alreadyOnLoginPage is true (the deliberate idle-timeout flow)', () => {
    routerUrl = '/auth/login';
    const fire = stubSwalFire();

    service.show('Your session has expired due to inactivity.', true);

    expect(fire).toHaveBeenCalledTimes(1);
  });

  it('never opens a second prompt while one is already open', () => {
    routerUrl = '/workflows';
    const fire = stubSwalFire();

    service.show();
    service.show();

    expect(fire).toHaveBeenCalledTimes(1);
  });

  it('auto-closes an open prompt once navigation lands on the login page', () => {
    routerUrl = '/workflows';
    stubSwalFire();
    const close = spyOn(Swal, 'close');

    service.show(); // opened over /workflows, e.g. the interceptor's 401 path

    routerEvents.next(new NavigationEnd(1, '/auth/login', '/auth/login'));

    expect(close).toHaveBeenCalled();
  });

  it('does not touch Swal on a navigation to a non-login page while a prompt is open', () => {
    routerUrl = '/workflows';
    stubSwalFire();
    const close = spyOn(Swal, 'close');

    service.show();

    routerEvents.next(new NavigationEnd(1, '/dashboard', '/dashboard'));

    expect(close).not.toHaveBeenCalled();
  });

  it('does not call Swal.close() on a login-page navigation when nothing is open', () => {
    const close = spyOn(Swal, 'close');

    routerEvents.next(new NavigationEnd(1, '/auth/login', '/auth/login'));

    expect(close).not.toHaveBeenCalled();
  });
});
