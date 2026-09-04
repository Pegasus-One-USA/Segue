import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';

export interface FieldDiff {
  field: string;
  oldValue: string;
  newValue: string;
  changeType: 'added' | 'removed' | 'changed';
}

export interface DiffDetailDialogData {
  module: string;
  action: string;
  diffs: FieldDiff[];
}

@Component({
  selector: 'app-diff-detail-dialog',
  standalone: true,
  imports: [CommonModule, MatDialogModule, MatIconModule],
  templateUrl: './diff-detail-dialog.component.html',
  styleUrls: ['./diff-detail-dialog.component.scss'],
})
export class DiffDetailDialogComponent {
  readonly dialogRef = inject<DialogRef<void>>(DialogRef);
  readonly data: DiffDetailDialogData = inject(DIALOG_DATA) as DiffDetailDialogData;
}
