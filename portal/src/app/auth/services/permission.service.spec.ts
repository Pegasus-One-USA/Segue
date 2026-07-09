import { TestBed } from '@angular/core/testing';
import { PermissionService } from './permission.service';
import { AuthStore } from '../store/auth.store';
import { makeTestUser as makeUser } from '../testing/auth-test-helpers';

describe('PermissionService', () => {
  let service: PermissionService;
  let authStore: AuthStore;

  beforeEach(() => {
    // AuthStore has no dependencies of its own (pure signals), so using the real class
    // — rather than a hand-rolled mock — is both simpler and a truer integration check.
    TestBed.configureTestingModule({ providers: [AuthStore, PermissionService] });
    service   = TestBed.inject(PermissionService);
    authStore = TestBed.inject(AuthStore);
  });

  describe('when no user is logged in', () => {
    it('hasPermission is always false (fails closed, never throws)', () => {
      expect(service.hasPermission('epic.edit')).toBeFalse();
    });

    it('hasAny/hasAll are false, hasNone is true, for an empty user', () => {
      expect(service.hasAny(['epic.edit'])).toBeFalse();
      expect(service.hasAll(['epic.edit'])).toBeFalse();
      expect(service.hasNone(['epic.edit'])).toBeTrue();
    });
  });

  describe('single-permission checks', () => {
    beforeEach(() => authStore.setUser(makeUser(['epic.edit', 'workflow.run'])));

    it('returns true for a held permission', () => {
      expect(service.hasPermission('epic.edit')).toBeTrue();
    });

    it('returns false for a permission not held', () => {
      expect(service.hasPermission('epic.delete')).toBeFalse();
    });

    it('is case- and whitespace-insensitive (defensive normalization)', () => {
      expect(service.hasPermission('  Epic.EDIT ')).toBeTrue();
    });
  });

  describe('hasAny / hasAll / hasNone', () => {
    beforeEach(() => authStore.setUser(makeUser(['epic.read', 'epic.edit'])));

    it('hasAny is true if at least one code matches (OR)', () => {
      expect(service.hasAny(['epic.delete', 'epic.edit'])).toBeTrue();
    });

    it('hasAny is false if none match', () => {
      expect(service.hasAny(['epic.delete', 'cerner.edit'])).toBeFalse();
    });

    it('hasAll is true only when every code matches (AND)', () => {
      expect(service.hasAll(['epic.read', 'epic.edit'])).toBeTrue();
      expect(service.hasAll(['epic.read', 'epic.delete'])).toBeFalse();
    });

    it('hasAll is vacuously true for an empty list', () => {
      expect(service.hasAll([])).toBeTrue();
    });

    it('hasNone is true only when no code matches', () => {
      expect(service.hasNone(['epic.delete', 'cerner.edit'])).toBeTrue();
      expect(service.hasNone(['epic.delete', 'epic.edit'])).toBeFalse();
    });
  });

  describe('admin bypass', () => {
    beforeEach(() => authStore.setUser(makeUser([], 'SuperAdmin')));

    it('hasPermission/hasAny/hasAll are true for any code, even ones that do not exist', () => {
      expect(service.hasPermission('anything.goes')).toBeTrue();
      expect(service.hasAny(['a.b'])).toBeTrue();
      expect(service.hasAll(['a.b', 'c.d'])).toBeTrue();
    });

    it('hasNone is correctly FALSE for an admin — they hold every permission, including the ones being asked about', () => {
      // Regression guard for the exact bug described in permission.service.ts's hasNone() doc
      // comment: naively short-circuiting every has* method to `true` for admins would make
      // this wrongly return true and hide a "you lack this permission" banner from everyone
      // except the one role that should never see it.
      expect(service.hasNone(['epic.edit'])).toBeFalse();
    });
  });

  describe('duplicate permissions', () => {
    it('de-duplicates automatically via the underlying Set', () => {
      authStore.setUser(makeUser(['epic.edit', 'epic.edit', 'EPIC.EDIT']));
      expect(service.permissions().length).toBe(1);
    });
  });

  describe('reactivity', () => {
    it('reflects a logout immediately', () => {
      authStore.setUser(makeUser(['epic.edit']));
      expect(service.hasPermission('epic.edit')).toBeTrue();

      authStore.clear();
      expect(service.hasPermission('epic.edit')).toBeFalse();
    });

    it('reflects a permission set gained on token refresh (new User object)', () => {
      authStore.setUser(makeUser(['epic.read']));
      expect(service.hasPermission('epic.edit')).toBeFalse();

      authStore.setUser(makeUser(['epic.read', 'epic.edit']));
      expect(service.hasPermission('epic.edit')).toBeTrue();
    });
  });
});
