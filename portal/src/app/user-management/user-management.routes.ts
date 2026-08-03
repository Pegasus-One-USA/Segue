import { Routes } from '@angular/router';
import { authGuard } from '../auth/guards/auth.guard';
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
    path: 'roles',
    canActivate: [authGuard],
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
