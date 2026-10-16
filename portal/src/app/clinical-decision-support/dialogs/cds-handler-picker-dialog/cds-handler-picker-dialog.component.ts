import { Component, inject } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';
import { CdsHandler } from '../../models/cds.models';
import { cdsHookLabel } from '../../models/cds-labels';

export interface CdsHandlerPickerDialogData {
  handlers: CdsHandler[];
}

/** "Add service": pick the built-in check the new service runs. Closes with the chosen handler key. */
@Component({
  selector: 'app-cds-handler-picker-dialog',
  standalone: true,
  imports: [MatIconModule],
  templateUrl: './cds-handler-picker-dialog.component.html',
  styleUrl: './cds-handler-picker-dialog.component.scss',
})
export class CdsHandlerPickerDialogComponent {
  readonly dialogRef = inject<DialogRef<string>>(DialogRef);
  readonly data = inject(DIALOG_DATA) as CdsHandlerPickerDialogData;

  readonly hookLabel = cdsHookLabel;

  pick(handler: CdsHandler): void {
    this.dialogRef.close(handler.key);
  }
}
