// user-management/dialogs/invite-user-dialog/invite-user-dialog.component.ts
import { Component, OnInit, signal, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, FormGroup, Validators } from '@angular/forms';

import { MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatDividerModule } from '@angular/material/divider';

import { ToastService } from '../../../services/toast.service';
import { IUserService } from '../../../auth/services/i-user.service';
import { Role, UserRole } from '../../../auth/models/user.model';
import { InviteUserRequest } from '../../../auth/models/auth-request.model';
import { InviteResultDialogComponent } from '../invite-result-dialog/invite-result-dialog.component';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { PermissionGroup, PermissionAction, permissionCode } from '../../../auth/models/permission.constants';
import { DialogService, DialogRef } from '../../../core/services/dialog.service';
import { InviteResult } from '../../../auth/models/user.model';

@Component({
  selector: 'app-invite-user-dialog',
  standalone: true,
  imports: [
    CommonModule,
    ReactiveFormsModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatButtonModule,
    MatIconModule,
    MatProgressSpinnerModule,
    MatDividerModule,
  ],
  templateUrl: './invite-user-dialog.component.html',
  styleUrls: ['./invite-user-dialog.component.scss'],
})
// No hasUnsavedChanges()/attemptClose() here — DialogRef.attemptClose() (dialog.service.ts) generically
// duck-types this component's own `form` property (present below) and reads its `.dirty` automatically.
export class InviteUserDialogComponent implements OnInit {
  private readonly userService = inject(IUserService);
  readonly dialogRef           = inject<DialogRef<InviteResult | null>>(DialogRef);
  private readonly customDialog = inject(DialogService);
  private readonly toast       = inject(ToastService);
  private readonly fb          = inject(FormBuilder);
  private readonly actionGuard = inject(PermissionActionGuard);

  // ─── State ───────────────────────────────────────────────────────────────
  loading      = signal(false);
  submitted    = signal(false);
  rolesLoading = signal(true);
  roles        = signal<Role[]>([]);

  // ─── Form ────────────────────────────────────────────────────────────────
  form!: FormGroup;

  // ─── Lifecycle ───────────────────────────────────────────────────────────
  ngOnInit(): void {
    this.form = this.fb.group({
      email:     ['', [Validators.required, Validators.email]],
      firstName: ['', [Validators.maxLength(80)]],
      lastName:  ['', [Validators.maxLength(80)]],
      roleId:    ['', Validators.required],
    });

    this.loadRoles();
  }

  private loadRoles(): void {
    this.rolesLoading.set(true);
    this.userService.getRoles().subscribe({
      next: roles => {
        this.roles.set(roles);
        this.rolesLoading.set(false);
        // Default to a sensible non-admin role if present.
        const defaultRole = roles.find(r => r.name === 'Audit') ?? roles[0];
        if (defaultRole) this.form.get('roleId')!.setValue(defaultRole.id);
      },
      error: err => {
        this.rolesLoading.set(false);
        this.toast.error(err?.message ?? 'Failed to load roles.');
      },
    });
  }

  // ─── Helpers ─────────────────────────────────────────────────────────────
  hasError(field: string, error: string): boolean {
    const ctrl = this.form.get(field);
    return !!(ctrl && ctrl.hasError(error) && (ctrl.touched || this.submitted()));
  }

  // ─── Submit ──────────────────────────────────────────────────────────────
  submit(): void {
    // Defense-in-depth: the only way to reach this dialog is UserListComponent's/UserDetailComponent's
    // gated openInviteDialog()/resendInvitation()-adjacent triggers — this re-check guards against a
    // permission change landing in another tab while the dialog is still open, not a normal path.
    if (!this.actionGuard.ensure(permissionCode(PermissionGroup.User, PermissionAction.Invite), 'You do not have permission to invite users.')) return;
    this.submitted.set(true);
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.loading.set(true);
    const value = this.form.value;
    const selectedRole = this.roles().find(r => r.id === value.roleId);
    const req: InviteUserRequest = {
      email:     value.email.trim().toLowerCase(),
      firstName: value.firstName?.trim() ?? '',
      lastName:  value.lastName?.trim() ?? '',
      role:      (selectedRole?.name ?? 'Audit') as UserRole,
      roleId:    value.roleId,
    };

    this.userService.inviteUser(req).subscribe({
      next: res => {
        this.loading.set(false);
        const message = res.message ?? `Invitation sent to "${req.email}".`;
        if (res.emailSent) {
          this.toast.success(message);
        } else {
          this.toast.warning(message);
        }
        // Show the invitation link with a copy button.
        this.customDialog.open<InviteResultDialogComponent, InviteResult, void>(InviteResultDialogComponent, {
          width: '540px', data: res,
        });
        this.dialogRef.close(res);
      },
      error: err => {
        this.loading.set(false);
        this.toast.error(err?.message ?? 'Failed to send invitation. Please try again.');
      },
    });
  }

  isSaveInProgress(): boolean {
    return this.loading();
  }
}
