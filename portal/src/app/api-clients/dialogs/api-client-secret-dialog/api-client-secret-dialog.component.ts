import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';
import { API_V1_BASE, OAUTH_TOKEN_ENDPOINT } from '../../../core/api-endpoints';
import { ToastService } from '../../../services/toast.service';

export interface ApiClientSecretDialogData {
  clientId: string;
  plaintextSecret: string;
}

/**
 * Shown exactly once, right after Create or Regenerate Secret returns — this is the only time the
 * plaintext secret is ever available; the API never returns it again afterward. Includes a ready-to-run
 * code sample so an integrator can copy-paste the token request and the workflow trigger call immediately.
 */
@Component({
  selector: 'app-api-client-secret-dialog',
  standalone: true,
  imports: [CommonModule, MatDialogModule, MatButtonModule, MatIconModule, MatTooltipModule],
  templateUrl: './api-client-secret-dialog.component.html',
  styleUrls: ['./api-client-secret-dialog.component.scss'],
})
export class ApiClientSecretDialogComponent {
  private readonly toast = inject(ToastService);
  readonly dialogRef = inject<DialogRef<void>>(DialogRef);
  readonly data: ApiClientSecretDialogData = inject(DIALOG_DATA) as ApiClientSecretDialogData;

  readonly copiedField = signal<string | null>(null);
  readonly tokenEndpoint = OAUTH_TOKEN_ENDPOINT;

  // API_V1_BASE is absolute in development (separate API host) and empty in production (same origin), so resolve it
  // against the page origin rather than prefixing the origin to something that may already be absolute.
  private readonly apiBase = new URL(API_V1_BASE, window.location.origin).href.replace(/\/$/, '');

  readonly curlSample = `curl -X POST '${this.apiBase}/oauth/token' \\
  -H 'Content-Type: application/json' \\
  -d '{"grant_type":"client_credentials","client_id":"${this.data?.clientId}","client_secret":"${this.data?.plaintextSecret}"}'

# Then, with the returned access_token:
curl -X POST '${this.apiBase}/workflows/{workflowId}/run' \\
  -H 'Authorization: Bearer <access_token>' \\
  -H 'Content-Type: application/json' \\
  -d '{}'`;

  copyToClipboard(value: string, field: string): void {
    navigator.clipboard.writeText(value).then(
      () => {
        this.copiedField.set(field);
        setTimeout(() => this.copiedField.set(null), 2000);
      },
      () => this.toast.error('Could not copy to clipboard.'),
    );
  }

  close(): void {
    this.dialogRef.close();
  }
}
