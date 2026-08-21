import { Routes } from '@angular/router';
import { authGuard } from '../auth/guards/auth.guard';
import { permissionGuard } from '../auth/guards/permission.guard';
import { unsavedChangesGuard } from '../core/guards/unsaved-changes.guard';

export const USER_MANAGEMENT_ROUTES: Routes = [
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./pages/user-list/user-list.component').then(m => m.UserListComponent),
  },
  {
    path: 'tenants',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./pages/tenant-list/tenant-list.component').then(m => m.TenantListComponent),
  },
  {
    // Sidebar hides the "Role" entry unless the user has role.view (see sidebar.component.ts), but
    // routing here used to rely entirely on the parent /user-management route's own guard (user.view) —
    // meaning a role with user.view but not role.view could reach this page directly by URL even though
    // the nav link was hidden. Guarding role.view here too closes that gap.
    path: 'roles',
    canActivate: [permissionGuard],
    data: { permissions: ['role.view'] },
    loadComponent: () =>
      import('./pages/role-list/role-list.component').then(m => m.RoleListComponent),
  },
  {
    // Was `authGuard` only, inheriting just the parent /user-management route's `user.view` — meaning
    // a role with user.view but no role.view could open this screen directly by URL even though the
    // Roles list itself (one route up) already requires role.view. Guarding role.view here closes that
    // gap, matching the 'roles' route above. This only gates VIEWING the matrix — the mutating controls
    // inside it (Save, checkboxes) additionally require role.edit, enforced by the component itself, so
    // a role.view-only user can still open this route and see the read-only grid.
    path: 'roles/:id/permissions',
    canActivate: [permissionGuard],
    data: { permissions: ['role.view'] },
    canDeactivate: [unsavedChangesGuard],
    loadComponent: () =>
      import('./pages/role-permissions/role-permissions.component').then(m => m.RolePermissionsComponent),
  },
  {
    path: ':id',
    canActivate: [authGuard],
    canDeactivate: [unsavedChangesGuard],
    loadComponent: () =>
      import('./pages/user-detail/user-detail.component').then(m => m.UserDetailComponent),
  },
];
