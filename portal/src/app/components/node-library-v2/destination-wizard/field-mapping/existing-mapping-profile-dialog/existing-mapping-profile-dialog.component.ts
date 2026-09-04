import { Component, OnInit, signal, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { DIALOG_DATA, DialogRef } from '../../../../../core/services/dialog.service';
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
    <h2 mat-dialog-title class="empd-title">Select Existing Mapping Profile — {{ data.resourceType }}</h2>
    <mat-dialog-content class="empd-content">
      @if (loading()) {
        <div class="empd-loading"><mat-spinner diameter="32"></mat-spinner></div>
      } @else if (profiles().length === 0) {
        <div class="empd-empty">
          <p>No existing mapping profile found for this source connection, destination, and resource type.</p>
        </div>
      } @else {
        <div class="empd-table-wrap">
          <table class="empd-table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Destination object</th>
                <th class="empd-col-fields">Fields</th>
                <th class="empd-col-action"></th>
              </tr>
            </thead>
            <tbody>
              @for (p of profiles(); track p.id) {
                <tr>
                  <td class="empd-name">{{ p.name }}</td>
                  <td><span class="empd-dest-chip">{{ p.destinationObject }}</span></td>
                  <td class="empd-col-fields">{{ p.fields.length }}</td>
                  <td class="empd-col-action">
                    <button type="button" mat-flat-button color="primary" class="empd-use-btn" (click)="use(p)">Use</button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </mat-dialog-content>
  `,
  styles: [`
    .empd-title {
      padding: 20px 48px 18px 24px !important;
      margin: 0 !important;
      font-size: 17px;
      font-weight: 700;
      color: var(--color-ink);
      border-bottom: 1px solid var(--color-border);
    }
    .empd-content {
      min-width: 560px;
      max-width: 640px;
      padding: 0 !important;
    }
    .empd-loading {
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 48px 24px;
    }
    .empd-empty {
      padding: 40px 24px;
      text-align: center;

      p {
        margin: 0 auto;
        max-width: 360px;
        color: var(--color-muted);
        font-size: 14px;
        line-height: 1.6;
      }
    }
    .empd-table-wrap {
      padding: 8px 12px 20px;
      overflow-x: auto;
    }
    .empd-table {
      width: 100%;
      min-width: 480px;
      border-collapse: collapse;
    }
    .empd-table th {
      text-align: left;
      padding: 10px 12px;
      font-size: 11px;
      font-weight: 700;
      letter-spacing: 0.04em;
      text-transform: uppercase;
      color: var(--color-ink-2);
      background: var(--color-side);
      border-bottom: 1px solid var(--color-border);

      &:first-child { border-top-left-radius: var(--radius-sm, 6px); }
      &:last-child { border-top-right-radius: var(--radius-sm, 6px); }
    }
    .empd-table td {
      padding: 12px;
      font-size: 13.5px;
      color: var(--color-ink);
      border-bottom: 1px solid var(--color-border);
    }
    .empd-table tbody tr:last-child td { border-bottom: none; }
    .empd-table tbody tr:hover { background: var(--color-bg); }
    .empd-name { font-weight: 600; }
    .empd-dest-chip {
      display: inline-block;
      padding: 2px 8px;
      border-radius: var(--radius-pill, 999px);
      background: var(--color-primary-soft);
      color: var(--color-primary-dark);
      font-family: var(--font-mono, 'SFMono-Regular', Consolas, monospace);
      font-size: 12px;
    }
    .empd-col-fields { width: 72px; text-align: center; }
    .empd-col-action { width: 90px; text-align: right; }
    .empd-use-btn {
      height: 34px;
      min-width: 72px;
      font-weight: 600;
      border-radius: var(--radius-base);
    }
  `],
})
export class ExistingMappingProfileDialogComponent implements OnInit {
  readonly dialogRef = inject<DialogRef<MappingProfileDto | null>>(DialogRef);
  private readonly mappingProfileSvc = inject(MappingProfileService);
  private readonly toast = inject(ToastService);
  readonly data = inject<ExistingMappingProfileDialogData>(DIALOG_DATA);

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
}
