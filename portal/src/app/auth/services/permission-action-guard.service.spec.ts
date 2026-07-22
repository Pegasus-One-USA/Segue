import { TestBed } from '@angular/core/testing';
import { PermissionActionGuard } from './permission-action-guard.service';
import { AuthStore } from '../store/auth.store';
import { PermissionService } from './permission.service';
import { ToastService } from '../../services/toast.service';
import { makeTestUser } from '../testing/auth-test-helpers';

describe('PermissionActionGuard', () => {
  let guard: PermissionActionGuard;
  let authStore: AuthStore;
  let toast: jasmine.SpyObj<ToastService>;

  beforeEach(() => {
    toast = jasmine.createSpyObj('ToastService', ['success', 'error', 'warning', 'info']);
    TestBed.configureTestingModule({
      providers: [
        AuthStore,
        PermissionService,
        PermissionActionGuard,
        { provide: ToastService, useValue: toast },
      ],
    });
    guard     = TestBed.inject(PermissionActionGuard);
    authStore = TestBed.inject(AuthStore);
  });

  it('allows the action and shows no toast when the permission is held', () => {
    authStore.setUser(makeTestUser(['user.edit']));

    expect(guard.ensure('user.edit', 'You do not have permission to edit users.')).toBeTrue();
    expect(toast.warning).not.toHaveBeenCalled();
  });

  it('blocks the action and shows the given reason when the permission is missing', () => {
    authStore.setUser(makeTestUser([]));

    expect(guard.ensure('user.edit', 'You do not have permission to edit users.')).toBeFalse();
    expect(toast.warning).toHaveBeenCalledWith('You do not have permission to edit users.');
  });

  it('respects admin bypass — no toast, action proceeds', () => {
    authStore.setUser(makeTestUser([], 'SuperAdmin'));

    expect(guard.ensure('anything.goes', 'unused')).toBeTrue();
    expect(toast.warning).not.toHaveBeenCalled();
  });

  it('supports the same array + mode semantics as PermissionService', () => {
    authStore.setUser(makeTestUser(['epic.read']));

    expect(guard.ensure(['epic.read', 'epic.edit'], 'need all', 'all')).toBeFalse();
    expect(guard.ensure(['epic.read', 'epic.edit'], 'need any', 'any')).toBeTrue();
  });

  it('reflects a permission revoked after the control was rendered (the actual gap this closes)', () => {
    authStore.setUser(makeTestUser(['user.delete']));
    expect(guard.ensure('user.delete', 'nope')).toBeTrue();

    // Simulates another tab's session update (see CrossTabAuthSyncService) landing
    // between render and click — this tab's button hasn't re-rendered yet, but the
    // guard still catches it at the moment the action actually runs.
    authStore.setUser(makeTestUser([]));
    expect(guard.ensure('user.delete', 'You do not have permission to delete users.')).toBeFalse();
    expect(toast.warning).toHaveBeenCalledWith('You do not have permission to delete users.');
  });
});
