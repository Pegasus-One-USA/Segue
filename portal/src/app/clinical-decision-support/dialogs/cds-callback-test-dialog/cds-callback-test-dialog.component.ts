import { Component, OnInit, inject, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { MatIconModule } from '@angular/material/icon';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';
import { CdsApiService } from '../../services/cds-api.service';
import { CdsCallbackTestResult, CdsTrustedIssuer } from '../../models/cds.models';

export interface CdsCallbackTestDialogData {
  issuer: CdsTrustedIssuer;
}

/**
 * Checks Segue can reach an issuer's FHIR server: it asks for the server's capability statement (/metadata), with
 * no token and no patient data. Runs once when the dialog opens; "Test again" repeats it.
 */
@Component({
  selector: 'app-cds-callback-test-dialog',
  standalone: true,
  imports: [MatIconModule],
  templateUrl: './cds-callback-test-dialog.component.html',
  styleUrl: './cds-callback-test-dialog.component.scss',
})
export class CdsCallbackTestDialogComponent implements OnInit {
  private readonly api = inject(CdsApiService);
  readonly dialogRef = inject<DialogRef<void>>(DialogRef);
  readonly data = inject(DIALOG_DATA) as CdsCallbackTestDialogData;

  readonly running = signal(false);
  readonly result = signal<CdsCallbackTestResult | null>(null);
  readonly errorMessage = signal<string | null>(null);

  ngOnInit(): void {
    this.run();
  }

  run(): void {
    this.running.set(true);
    this.result.set(null);
    this.errorMessage.set(null);
    this.api.testCallback(this.data.issuer.id).subscribe({
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

  close(): void {
    this.dialogRef.close();
  }
}
