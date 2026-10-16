import { Injectable, inject } from '@angular/core';
import { Observable, map, of } from 'rxjs';
import { DialogService } from '../../core/services/dialog.service';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../core/components/confirm-dialog/confirm-dialog.component';

/**
 * Before a CDS service is saved: asks once when the save turns one-click fixes ON (they were off in the last saved
 * version). From then on, a clinician's click on a card changes their draft orders or problem list in the EHR, so
 * the question is asked at save time, like the live EHR write question in the workflow builder. Emits true to go
 * ahead, false when the admin cancels or closes the question (nothing is saved).
 */
@Injectable({ providedIn: 'root' })
export class CdsFixesConfirmService {
  private readonly dialogs = inject(DialogService);

  confirmSave(wasEnabled: boolean, willBeEnabled: boolean): Observable<boolean> {
    if (wasEnabled || !willBeEnabled) return of(true);
    return this.dialogs
      .open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
        width: '440px',
        data: {
          title: 'Let this service change EHR drafts?',
          message:
            'Cards from this service will offer one-click fixes. When a clinician clicks one, the EHR adds or removes '
            + 'draft orders or adds a problem or diagnosis, using the codes you set. Check the codes with your clinical team first.',
          confirmLabel: 'Save',
          danger: true,
        },
      })
      .afterClosed()
      .pipe(map(confirmed => confirmed === true));
  }
}
