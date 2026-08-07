import { Component, OnInit, signal, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { ToastService } from '../../../../../services/toast.service';
import { MappingProfileService } from '../../../../../mapping-profiles/services/mapping-profile.service';
import { MappingProfileDto } from '../../../../../mapping-profiles/models/mapping-profile.model';

export interface ExistingMappingProfileDialogData {
  resourceType: string;
  sourceConnectionId: string;
  destinationId: string;
}

/** Lets the user reuse a MappingProfile already saved for this exact source connection + destination +
 *  resource type, instead of re-authoring field mappings from scratch. Only ever opened when all three
 *  are real (see DestinationWizardComponent.canSelectExistingProfile — a brand-new inline source has no
 *  sourceConnectionId until the whole workflow is built, so the "Select Existing" trigger stays hidden
 *  until an existing source connection was picked). */
@Component({
  selector: 'app-existing-mapping-profile-dialog',
  standalone: true,
  imports: [CommonModule, MatDialogModule, MatButtonModule, MatProgressSpinnerModule],
  template: `
    <h2 mat-dialog-title>Select Existing Mapping Profile — {{ data.resourceType }}</h2>
    <mat-dialog-content class="empd-content">
      @if (loading()) {
        <div class="empd-loading"><mat-spinner diameter="28"></mat-spinner></div>
      } @else if (profiles().length === 0) {
        <p class="empd-empty">
          No existing mapping profile found for this source connection, destination, and resource type.
        </p>
      } @else {
        <table class="empd-table">
          <thead>
            <tr>
              <th>Name</th>
              <th>Destination object</th>
              <th>Fields</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            @for (p of profiles(); track p.id) {
              <tr>
                <td>{{ p.name }}</td>
                <td>{{ p.destinationObject }}</td>
                <td>{{ p.fields.length }}</td>
                <td class="empd-actions">
                  <button type="button" mat-flat-button color="primary" (click)="use(p)">Use</button>
                </td>
              </tr>
            }
          </tbody>
        </table>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button type="button" mat-button (click)="cancel()">Cancel</button>
    </mat-dialog-actions>
  `,
  styles: [`
    .empd-content { min-width: 480px; }
    .empd-loading { display: flex; justify-content: center; padding: 24px; }
    .empd-empty { color: var(--text-muted, #666); padding: 8px 0; }
    .empd-table { width: 100%; border-collapse: collapse; }
    .empd-table th, .empd-table td { text-align: left; padding: 8px 12px; border-bottom: 1px solid rgba(0,0,0,.08); }
    .empd-actions { text-align: right; }
  `],
})
export class ExistingMappingProfileDialogComponent implements OnInit {
  private readonly dialogRef = inject(MatDialogRef<ExistingMappingProfileDialogComponent>);
  private readonly mappingProfileSvc = inject(MappingProfileService);
  private readonly toast = inject(ToastService);
  readonly data = inject<ExistingMappingProfileDialogData>(MAT_DIALOG_DATA);

  readonly loading = signal(true);
  readonly profiles = signal<MappingProfileDto[]>([]);

  ngOnInit(): void {
    this.mappingProfileSvc.getPaged({
      resourceType: this.data.resourceType,
      sourceConnectionId: this.data.sourceConnectionId,
      destinationId: this.data.destinationId,
      isEnabled: true,
      page: 1,
      pageSize: 50,
    }).subscribe({
      next: result => {
        this.profiles.set(result.items);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.toast.error('Failed to load existing mapping profiles.');
      },
    });
  }

  use(profile: MappingProfileDto): void {
    this.dialogRef.close(profile);
  }

  cancel(): void {
    this.dialogRef.close(null);
  }
}
