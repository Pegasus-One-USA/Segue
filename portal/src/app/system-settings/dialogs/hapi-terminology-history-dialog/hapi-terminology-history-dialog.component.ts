import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { MatDialogModule } from '@angular/material/dialog';
import { TerminologyImportHistoryComponent, TerminologyImportHistoryEntry } from '../../../settings/components/terminology-import-history/terminology-import-history.component';
import { TerminologyHistoryPoller } from '../../../settings/utils/terminology-history-poller';
import { HapiTerminologyConfigurationService } from '../../services/hapi-terminology-configuration.service';
import { DIALOG_DATA } from '../../../core/services/dialog.service';

export interface HapiTerminologyHistoryDialogData {
  code: string;
  displayName: string;
}

/** Read-only History popup for one HAPI-terminology-server sync system — hosts the same shared
 * TerminologyImportHistoryComponent the legacy per-system tabs use, backed by its own poller
 * instance so a Running row updates live while this dialog is open. */
@Component({
  selector: 'app-hapi-terminology-history-dialog',
  standalone: true,
  imports: [MatDialogModule, TerminologyImportHistoryComponent],
  templateUrl: './hapi-terminology-history-dialog.component.html',
  styleUrls: ['./hapi-terminology-history-dialog.component.scss'],
})
export class HapiTerminologyHistoryDialogComponent implements OnInit, OnDestroy {
  private readonly svc = inject(HapiTerminologyConfigurationService);
  readonly data = inject<HapiTerminologyHistoryDialogData>(DIALOG_DATA);

  readonly history = signal<TerminologyImportHistoryEntry[]>([]);
  readonly loading = signal(false);

  private readonly poller = new TerminologyHistoryPoller<TerminologyImportHistoryEntry>(
    () => this.svc.getHistory(this.data.code),
    this.history,
    this.loading,
    () => {},
  );

  ngOnInit(): void {
    this.poller.load();
  }

  ngOnDestroy(): void {
    this.poller.dispose();
  }
}
