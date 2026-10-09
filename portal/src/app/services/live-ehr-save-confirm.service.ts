import { Injectable, inject } from '@angular/core';
import { Observable, map, of } from 'rxjs';
import { DialogService } from '../core/services/dialog.service';
import { ConfirmDialogComponent, ConfirmDialogData } from '../core/components/confirm-dialog/confirm-dialog.component';
import { LiveEhrWriteTarget, addedLiveEhrWriteTargets, ehrNamesOf } from './live-ehr-write-targets.util';

/**
 * Before the workflow builder saves: asks once when the save would ADD live EHR writing that the last saved version
 * did not have (a new live write-back, a test or dry run switched to live, or a live write-back now writing into
 * another EHR or through another connection). Once saved, every run writes, scheduled and triggered runs included,
 * so the question is asked here rather than only before a manual Run. Emits true to go ahead with the save, false
 * when the admin cancels (or closes the question), in which case nothing is saved.
 */
@Injectable({ providedIn: 'root' })
export class LiveEhrSaveConfirmService {
  private readonly dialogs = inject(DialogService);

  confirmSave(saved: readonly LiveEhrWriteTarget[], saving: readonly LiveEhrWriteTarget[]): Observable<boolean> {
    if (addedLiveEhrWriteTargets(saved, saving).length === 0) return of(true);
    const ehr = ehrNamesOf(saving.map(target => target.vendor));
    return this.dialogs
      .open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
        width: '440px',
        data: {
          title: `Write into ${ehr} on every run?`,
          message: `This workflow will write records into ${ehr} every time it runs, including scheduled and triggered runs. This cannot be undone.`,
          confirmLabel: 'Save',
          danger: true,
        },
      })
      .afterClosed()
      .pipe(map(confirmed => confirmed === true));
  }
}
