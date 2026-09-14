import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { IRoleService } from '../../services/i-role.service';
import { Role } from '../../../auth/models/user.model';
import { AuthService } from '../../../auth/services/auth.service';
import { FullAccessResolverService } from '../../../auth/services/full-access-resolver.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { PermissionGroup, PermissionAction, permissionCode } from '../../../auth/models/permission.constants';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';

export interface RoleDialogData {
  role?: Role;
}

@Component({
  selector: 'app-role-dialog',
  standalone: true,
  imports: [
    CommonModule,
    ReactiveFormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatSlideToggleModule,
  ],
  templateUrl: './role-dialog.component.html',
  styleUrls: ['./role-dialog.component.scss'],
})
// No hasUnsavedChanges()/attemptClose() here — DialogRef.attemptClose() (dialog.service.ts) generically
// duck-types this component's own `form` property (present below) and reads its `.dirty` automatically,
// so every plain single-reactive-form dialog gets "Discard changes?" gating for free with zero
// boilerplate. isSaveInProgress() is kept explicit below since "a save is actually in flight" isn't
// something a generic dirty-check can infer from a form alone.
export class RoleDialogComponent {
  private readonly svc           = inject(IRoleService);
  private readonly fb            = inject(FormBuilder);
  private readonly actionGuard   = inject(PermissionActionGuard);
  private readonly authService   = inject(AuthService);
  private readonly fullAccessSvc = inject(FullAccessResolverService);
  readonly dialogRef   = inject<DialogRef<boolean>>(DialogRef);
  readonly data: RoleDialogData = inject(DIALOG_DATA) as RoleDialogData;

  readonly isEdit       = !!this.data?.role;
  readonly isSystemRole = !!this.data?.role?.isSystemRole;
  readonly submitted    = signal(false);
  readonly saving       = signal(false);
  readonly errorMessage = signal<string | null>(null);

  // RBAC redesign Step 6: whether the ACTING/current user holds Full System Access themselves —
  // resolved via the shared FullAccessResolverService (see that file for why it matches by role
  // NAME, never displayName or id), cross-referenced against the roles this session's own claims say
  // it holds (authService.roles()) — never a hardcoded SuperAdmin/Admin name check, and never a
  // role-name check at all: a future custom role marked Full System Access satisfies this
  // identically. Only such a caller may edit the toggle below; the backend
  // (RoleManagementService.CreateRoleAsync/UpdateRoleAsync) independently and authoritatively
  // enforces the exact same rule regardless of what this renders as — this is UX only.
  readonly callerHasFullAccess = signal(false);

  readonly form = this.fb.group({
    name: [
      { value: this.data?.role?.displayName ?? '', disabled: this.isSystemRole },
      [Validators.required, Validators.maxLength(120)],
    ],
    description: [
      { value: this.data?.role?.description ?? '', disabled: this.isSystemRole },
      [Validators.required, Validators.maxLength(300)],
    ],
    // Starts disabled — flipped to enabled only once callerHasFullAccess resolves true below, and
    // only for a non-system role (system roles keep the same fully-locked shape as name/description
    // above; see RoleManagementService.UpdateRoleAsync's own SuperAdmin-only full lock plus this
    // dialog's existing isSystemRole gate for Admin/Operations/Audit). Defaulting to disabled means a
    // caller can never toggle this before the check has actually come back.
    isFullAccess: [{ value: this.data?.role?.isFullAccess ?? false, disabled: true }],
  });

  constructor() {
    // RBAC Fix 9: fail closed on API failure — the shared resolver resolves `false` (never errors) on
    // any getRoles() failure, so callerHasFullAccess stays/becomes false and the toggle stays disabled
    // (its constructed default), exactly as for a caller who simply isn't Full Access.
    const heldRoleNames = new Set(this.authService.roles().map(r => r.name));
    this.fullAccessSvc.resolve(heldRoleNames).subscribe(hasFullAccess => {
      this.callerHasFullAccess.set(hasFullAccess);
      if (hasFullAccess && !this.isSystemRole) {
        this.form.controls.isFullAccess.enable();
      }
    });
  }

  hasError(ctrl: string, err: string): boolean {
    const c = this.form.get(ctrl)!;
    return c.hasError(err) && (c.touched || this.submitted());
  }

  isSaveInProgress(): boolean {
    return this.saving();
  }

  save(): void {
    if (this.isSystemRole) return;
    // Defense-in-depth: the only way to reach this dialog today is via RoleListComponent's
    // openAdd()/openEdit(), which already gate on the same codes and hide their triggering buttons
    // entirely without permission — this re-check guards against a permission change landing in
    // another tab while this dialog is still open, not against a normally-reachable gap.
    const requiredCode = this.isEdit
      ? permissionCode(PermissionGroup.Role, PermissionAction.Edit)
      : permissionCode(PermissionGroup.Role, PermissionAction.Create);
    if (!this.actionGuard.ensure(requiredCode, `You do not have permission to ${this.isEdit ? 'edit' : 'create'} roles.`)) return;
    this.submitted.set(true);
    this.errorMessage.set(null);
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }

    const { name, description, isFullAccess } = this.form.getRawValue() as
      { name: string; description: string; isFullAccess: boolean };
    this.saving.set(true);

    // updateRole replaces the role's full permission set, so name/description-only edits here
    // must resend the role's current permissions unchanged — the Permissions screen owns those.
    const permissionIds = this.data?.role?.permissions?.map(p => p.id) ?? [];

    // isFullAccess is always sent as the form's current (raw, disabled-or-not) value — when the
    // toggle isn't editable it's unchanged from what was loaded, and the backend treats an unchanged
    // value as a pure no-op regardless of the caller's own access (see UpdateRoleRequest.isFullAccess's
    // doc comment). When it IS editable and was actually flipped, the backend independently verifies
    // the caller has Full System Access before honoring it — this dialog never makes that decision
    // itself, only reflects it in the UI.
    const request$ = this.isEdit
      ? this.svc.updateRole(this.data.role!.id, { name, description, permissionIds, isFullAccess })
      : this.svc.createRole({ name, description, permissionIds: [], isFullAccess });

    request$.subscribe({
      next: () => {
        this.saving.set(false);
        this.dialogRef.close(true);
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.errorMessage.set(err.error?.title ?? 'Failed to save role. Please try again.');
      },
    });
  }
}
