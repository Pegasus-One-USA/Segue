import { Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CdsSetupSheetCrd } from '../../models/cds.models';
import { cdsHookLabel } from '../../models/cds-labels';
import { CdsCopyButtonComponent } from '../cds-copy-button/cds-copy-button.component';

/** The order-update API Epic needs before Segue can write coverage information onto an order. */
export const CDS_EPIC_ORDER_UPDATE_API = 'ServiceRequest.Update (Unsigned Order)';

/**
 * The coverage requirements (Da Vinci CRD) part of the Epic setup sheet: the hook for Epic's "CRD Request" call,
 * the Epic APIs the client registration needs, the documentation app to allow-list, Segue's key set for payers that
 * receive relayed calls, the payers this service answers for, and notes for the Epic team.
 */
@Component({
  selector: 'app-cds-setup-sheet-crd',
  standalone: true,
  imports: [RouterLink, CdsCopyButtonComponent],
  templateUrl: './cds-setup-sheet-crd.component.html',
  styleUrl: './cds-setup-sheet-crd.component.scss',
})
export class CdsSetupSheetCrdComponent {
  readonly crd = input.required<CdsSetupSheetCrd>();
  /** The block number in the sheet. */
  readonly step = input(7);

  readonly hookLabel = cdsHookLabel;
  readonly orderUpdateApi = CDS_EPIC_ORDER_UPDATE_API;

  readonly anyRelay = computed(() => this.crd().payers.some(p => p.relayEnabled));

  isOrderUpdate(api: string): boolean {
    return api.toLowerCase().startsWith('servicerequest.update');
  }
}
