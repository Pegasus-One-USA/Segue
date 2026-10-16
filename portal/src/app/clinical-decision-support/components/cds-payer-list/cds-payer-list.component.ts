import { Component, inject, input, output } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { CdsCoverageApiService } from '../../services/cds-coverage-api.service';
import { CdsPayer } from '../../models/cds-coverage.models';
import { CdsPayerDialogComponent, CdsPayerDialogData } from '../../dialogs/cds-payer-dialog/cds-payer-dialog.component';
import { CdsRelayTestDialogComponent, CdsRelayTestDialogData } from '../../dialogs/cds-relay-test-dialog/cds-relay-test-dialog.component';
import { DialogService } from '../../../core/services/dialog.service';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../../core/components/confirm-dialog/confirm-dialog.component';
import { ToastService } from '../../../services/toast.service';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { PermissionService } from '../../../auth/services/permission.service';
import { HideWithoutPermissionDirective } from '../../../auth/directives/hide-without-permission.directive';

/**
 * The payers Segue answers coverage questions for: how each is recognised, how many rules it has, and whether its
 * calls are answered from those rules or relayed to the payer's own service. Add, edit, test relay and delete.
 */
@Component({
  selector: 'app-cds-payer-list',
  standalone: true,
  imports: [MatIconModule, MatButtonModule, MatMenuModule, MatTooltipModule, HideWithoutPermissionDirective],
  templateUrl: './cds-payer-list.component.html',
  styleUrl: './cds-payer-list.component.scss',
})
export class CdsPayerListComponent {
  private readonly api         = inject(CdsCoverageApiService);
  private readonly dialog      = inject(DialogService);
  private readonly toast       = inject(ToastService);
  private readonly actionGuard = inject(PermissionActionGuard);
  private readonly permissions = inject(PermissionService);

  readonly payers = input.required<CdsPayer[]>();
  /** Fired after any add, edit or delete so the page reloads. */
  readonly changed = output<void>();

  /** Test relay is offered only for a relayed payer, so a test-only role sees a menu only there. */
  hasRowMenu(payer: CdsPayer): boolean {
    return this.permissions.hasPermission('cdshooks.edit')
      || this.permissions.hasPermission('cdshooks.delete')
      || (payer.relayEnabled && this.permissions.hasPermission('cdshooks.test'));
  }

  openAdd(): void {
    if (!this.actionGuard.ensure('cdshooks.create', 'You do not have permission to add payers.')) return;
    this.openDialog({}, 'added');
  }

  openEdit(payer: CdsPayer): void {
    if (!this.actionGuard.ensure('cdshooks.edit', 'You do not have permission to edit payers.')) return;
    this.openDialog({ payer }, 'saved');
  }

  openRelayTest(payer: CdsPayer): void {
    if (!this.actionGuard.ensure('cdshooks.test', 'You do not have permission to test payer connections.')) return;
    this.dialog.open<CdsRelayTestDialogComponent, CdsRelayTestDialogData, void>(CdsRelayTestDialogComponent, {
      width: '560px',
      data: { payer },
    });
  }

  confirmDelete(payer: CdsPayer): void {
    if (!this.actionGuard.ensure('cdshooks.delete', 'You do not have permission to delete payers.')) return;
    const rules = payer.ruleCount === 1 ? 'its 1 rule' : `its ${payer.ruleCount} rules`;
    this.dialog
      .open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
        width: '460px',
        data: {
          title: 'Delete payer',
          message: payer.ruleCount > 0
            ? `Delete "${payer.name}" and ${rules}? Clinicians will no longer see coverage requirements for this payer.`
            : `Delete "${payer.name}"? Clinicians will no longer see coverage requirements for this payer.`,
          confirmLabel: 'Delete',
          danger: true,
        },
      })
      .afterClosed()
      .subscribe(confirmed => {
        if (!confirmed) return;
        this.api.deletePayer(payer.id).subscribe({
          next: () => {
            this.toast.success(`"${payer.name}" deleted.`);
            this.changed.emit();
          },
          error: (err: HttpErrorResponse) => this.toast.error(err.message || 'Could not delete the payer.'),
        });
      });
  }

  /** How the payer's calls are answered, in a few words. */
  answeredBy(payer: CdsPayer): string {
    if (!payer.relayEnabled) return 'Rules in Segue';
    return payer.fallbackToLocalRules ? "Payer's service, then rules in Segue" : "Payer's service only";
  }

  private openDialog(data: CdsPayerDialogData, verb: string): void {
    this.dialog
      .open<CdsPayerDialogComponent, CdsPayerDialogData, CdsPayer>(CdsPayerDialogComponent, {
        width: '680px',
        disableClose: true,
        data,
      })
      .afterClosed()
      .subscribe(saved => {
        if (!saved) return;
        this.toast.success(`"${saved.name}" ${verb}.`);
        this.changed.emit();
      });
  }
}
