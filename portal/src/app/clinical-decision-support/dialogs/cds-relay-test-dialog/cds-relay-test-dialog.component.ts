import { Component, OnInit, inject, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { MatIconModule } from '@angular/material/icon';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';
import { CdsCoverageApiService } from '../../services/cds-coverage-api.service';
import { CdsPayer, CdsPayerRelayTestResult } from '../../models/cds-coverage.models';

export interface CdsRelayTestDialogData {
  payer: CdsPayer;
}

/**
 * Checks Segue can reach a payer's own coverage requirements service: it sends a made-up order (no patient data),
 * signed with Segue's key, and shows how the payer answered. Runs once on open; "Test again" repeats it.
 */
@Component({
  selector: 'app-cds-relay-test-dialog',
  standalone: true,
  imports: [MatIconModule],
  templateUrl: './cds-relay-test-dialog.component.html',
  styleUrl: './cds-relay-test-dialog.component.scss',
})
export class CdsRelayTestDialogComponent implements OnInit {
  private readonly api = inject(CdsCoverageApiService);
  readonly dialogRef = inject<DialogRef<void>>(DialogRef);
  readonly data = inject(DIALOG_DATA) as CdsRelayTestDialogData;

  readonly running = signal(false);
  readonly result = signal<CdsPayerRelayTestResult | null>(null);
  readonly errorMessage = signal<string | null>(null);

  ngOnInit(): void {
    this.run();
  }

  run(): void {
    this.running.set(true);
    this.result.set(null);
    this.errorMessage.set(null);
    this.api.testRelay(this.data.payer.id).subscribe({
      next: result => {
        this.running.set(false);
        this.result.set(result);
      },
      error: (err: HttpErrorResponse) => {
        this.running.set(false);
        this.errorMessage.set(err.message || 'The test could not be run.');
      },
    });
  }

  /** "ok" reads as "Answered"; failure reasons are already short phrases ("timed out", "HTTP 401"). */
  outcomeLabel(result: CdsPayerRelayTestResult): string {
    if (result.outcome === 'ok') return 'Answered';
    return result.outcome.charAt(0).toUpperCase() + result.outcome.slice(1);
  }

  close(): void {
    this.dialogRef.close();
  }
}
