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
    path: 'roles/:id/permissions',
    canActivate: [authGuard],
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
