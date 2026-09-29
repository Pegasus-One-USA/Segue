import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { Router } from '@angular/router';
import { of } from 'rxjs';
import { SessionService } from './session.service';
import { AuthStore } from '../store/auth.store';
import { TokenService } from './token.service';
import { IAuthService } from './i-auth.service';
import { SessionExpiredDialogService } from './session-expired-dialog.service';
import { MappingSnapshotService } from '../../components/node-library-v2/destination-wizard/field-mapping/mapping-snapshot.service';

// Mirrors SessionService's own private IDLE_MS -- not exported, so duplicated here deliberately;
// this is the same 30-minute inactivity window the class's own doc comments describe.
const IDLE_MS = 30 * 60 * 1000;

describe('SessionService', () => {
  let service: SessionService;
  let store: AuthStore;
  let sessionExpired: jasmine.SpyObj<SessionExpiredDialogService>;
  let router: jasmine.SpyObj<Router>;

  beforeEach(() => {
    sessionExpired = jasmine.createSpyObj<SessionExpiredDialogService>('SessionExpiredDialogService', ['show']);
    router = jasmine.createSpyObj<Router>('Router', ['navigate']);
    router.navigate.and.returnValue(Promise.resolve(true));

    TestBed.configureTestingModule({
      providers: [
        AuthStore,
        TokenService,
        MappingSnapshotService,
        { provide: IAuthService, useValue: { logout: () => of(undefined) } },
        { provide: SessionExpiredDialogService, useValue: sessionExpired },
        { provide: Router, useValue: router },
      ],
    });

    service = TestBed.inject(SessionService);
    store = TestBed.inject(AuthStore);
  });

  afterEach(() => service.end());

  it('does not set a store error on idle timeout -- the SweetAlert prompt is the single source of that message', fakeAsync(() => {
    service.start('user-1');

    tick(IDLE_MS);
    tick(); // flush the logout()/navigate() promise chain inside onIdle()

    expect(store.error()).toBeNull();
    expect(router.navigate).toHaveBeenCalledWith(['/auth/login']);
    expect(sessionExpired.show).toHaveBeenCalledWith(
      'Your session has expired due to inactivity. Please log in again to continue.', true,
    );
  }));
});
