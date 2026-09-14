import { Routes } from '@angular/router';
import { permissionGuard } from '../auth/guards/permission.guard';
import { unsavedChangesGuard } from '../core/guards/unsaved-changes.guard';

export const USER_MANAGEMENT_ROUTES: Routes = [
  {
    // User-DATA screen — guards user.view on its own now that the parent /user-management route admits
    // user.view OR role.view (so a role.view-only user reaching the Roles routes can't also open this).
    path: '',
    canActivate: [permissionGuard],
    data: { permissions: ['user.view'] },
    loadComponent: () =>
      import('./pages/user-list/user-list.component').then(m => m.UserListComponent),
  },
  {
    path: 'tenants',
    canActivate: [permissionGuard],
    data: { permissions: ['user.view'] },
    loadComponent: () =>
      import('./pages/tenant-list/tenant-list.component').then(m => m.TenantListComponent),
  },
  {
    // Role management is independent of user.view: reachable with role.view alone (the parent
    // /user-management route admits user.view OR role.view — see app.routes.ts). This route's own
    // role.view guard is what actually gates it; the sidebar "Role" link mirrors it (role.view).
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
    // User-DATA screen — guards user.view on its own (same reason as the user-list route above).
    path: ':id',
    canActivate: [permissionGuard],
    data: { permissions: ['user.view'] },
    canDeactivate: [unsavedChangesGuard],
    loadComponent: () =>
      import('./pages/user-detail/user-detail.component').then(m => m.UserDetailComponent),
  },
];
