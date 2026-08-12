import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { A11yModule } from '@angular/cdk/a11y';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';

export interface PromoteMappingProfileDialogData {
  resourceType: string;
  suggestedName: string;
}

/** "Mark as Master" — asks for a name for the new, independently-owned master mapping profile before
 *  promoting the currently-open resource's mapping. Always results in a NEW MappingProfile row on the
 *  backend (see IConfigurationService.PromoteMappingProfileToMasterAsync); this dialog only collects the
 *  name, it never overwrites an existing master. */
@Component({
  selector: 'app-promote-mapping-profile-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule, A11yModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>Mark as Master — {{ data.resourceType }}</h2>
    <mat-dialog-content class="pmpd-content">
      <p class="pmpd-hint">
        This saves the current mapping as a new, reusable master template. Other workflows can find it later
        via "Select Existing" — editing this workflow's own mapping afterward won't change the master.
      </p>
      <mat-form-field appearance="outline" class="pmpd-field">
        <mat-label>Master mapping name</mat-label>
        <input matInput [(ngModel)]="name" maxlength="200" cdkFocusInitial />
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button type="button" mat-button (click)="cancel()">Cancel</button>
      <button type="button" mat-flat-button color="primary" [disabled]="!name.trim()" (click)="confirm()">
        Save as master
      </button>
    </mat-dialog-actions>
  `,
  styles: [`
    .pmpd-content { min-width: 420px; }
    .pmpd-hint { color: var(--text-muted, #666); margin: 0 0 12px; }
    .pmpd-field { width: 100%; }
  `],
})
export class PromoteMappingProfileDialogComponent {
  private readonly dialogRef = inject(MatDialogRef<PromoteMappingProfileDialogComponent>);
  readonly data = inject<PromoteMappingProfileDialogData>(MAT_DIALOG_DATA);

  name = this.data.suggestedName;

  confirm(): void {
    const trimmed = this.name.trim();
    if (trimmed) this.dialogRef.close(trimmed);
  }

  cancel(): void {
    this.dialogRef.close(null);
  }
}
