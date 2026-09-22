import { Component, inject } from '@angular/core';
import { DatePipe } from '@angular/common';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';
import { LICENSE_UNLIMITED, LicenseHistoryEntry } from '../../models/license.model';

/** Read-only detail popup for one row of License History — every quota/restriction dimension the
 *  license carried, not just the summary columns the table shows. Opened from
 *  LicenseSettingsComponent by clicking a history row. */
@Component({
  selector: 'app-license-history-detail-dialog',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './license-history-detail-dialog.component.html',
  styleUrl: './license-history-detail-dialog.component.scss',
})
export class LicenseHistoryDetailDialogComponent {
  readonly dialogRef = inject<DialogRef<void>>(DialogRef);
  readonly entry: LicenseHistoryEntry = inject<LicenseHistoryEntry>(DIALOG_DATA);

  protected readonly LICENSE_UNLIMITED = LICENSE_UNLIMITED;

  protected formatLimit(value: number): string {
    return value === LICENSE_UNLIMITED ? 'Unlimited' : value.toLocaleString();
  }
}
