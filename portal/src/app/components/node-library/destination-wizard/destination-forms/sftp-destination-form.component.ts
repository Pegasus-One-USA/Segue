import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { DestinationSchemaService } from '../../../../services/destination-schema.service';
import { buildConnectionMetadata, buildSftpUri } from '../../../../destination-connections/utils/destination-connection-secret.util';
import { WizardDestinationFormApi } from './destination-form-api';

/**
 * Standalone Sftp DestinationType — a first-class "just deliver files here" destination, distinct from CSV's
 * own sftp delivery mode (CsvDestinationFormComponent) which is "CSV delivered via SFTP" and stays under the
 * Csv DestinationType. Same host/port/username/password/remote-folder shape as CSV's sftp delivery-mode
 * fields, using the same buildSftpUri util, just as its own form/DestinationType rather than a CSV sub-mode.
 */
@Component({
  selector: 'app-sftp-destination-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './sftp-destination-form.component.html',
  styleUrls: ['../destination-wizard.component.scss'],
})
export class SftpDestinationFormComponent implements WizardDestinationFormApi {
  private readonly fb = inject(FormBuilder);
  private readonly schemaSvc = inject(DestinationSchemaService);

  readonly sftpForm = this.fb.group({
    name: ['SFTP Destination', [Validators.required]],
    sftpHost: ['', [Validators.required]],
    sftpPort: [22, [Validators.required, Validators.min(1), Validators.max(65535)]],
    sftpUsername: ['', [Validators.required]],
    sftpAuthType: ['password', []],
    sftpPassword: ['', []],
    sftpRemoteFolder: ['', [Validators.required]],
  });

  /** True while the host is reusing a previously-saved connection unchanged — sftpPassword is a secret that
   *  is never repopulated when patching from an existing connection, so requiring it here would permanently
   *  block reuse unless the user retypes it just to satisfy validation. */
  readonly reusingExisting = input<boolean>(false);
  /** Set alongside reusingExisting — when present, testConnection() sends it instead of a blank password so
   *  the backend can resolve the stored secret server-side (see SftpDestinationConnectionTestService). */
  readonly existingDestinationId = input<string | null>(null);
  readonly probeState = signal<'idle' | 'testing' | 'ok' | 'error'>('idle');
  readonly probeError = signal<string | null>(null);

  constructor() {
    effect(() => {
      const requirePassword = !this.reusingExisting();
      untracked(() => {
        const ctrl = this.sftpForm.get('sftpPassword')!;
        ctrl.setValidators(requirePassword ? [Validators.required] : []);
        ctrl.updateValueAndValidity({ emitEvent: false });
      });
    });
  }

  isValid(): boolean {
    return this.sftpForm.valid;
  }

  getRawValue(): Record<string, unknown> {
    return this.sftpForm.getRawValue();
  }

  testConnection(): void {
    const v = this.sftpForm.value;
    const id = this.existingDestinationId();
    console.info(
      `[SFTP Test Connection] reusingExisting=${this.reusingExisting()} existingDestinationId=${id ?? '(none)'} ` +
      `passwordTyped=${!!v.sftpPassword} ` +
      `=> ${!v.sftpPassword && id ? 'resolving stored secret server-side' : 'using the form\'s own (typed) password'}`,
    );
    this.probeState.set('testing');
    this.probeError.set(null);
    this.schemaSvc.testSftp({
      host: v.sftpHost ?? '',
      port: v.sftpPort ?? 22,
      username: v.sftpUsername ?? '',
      password: v.sftpPassword ?? undefined,
      remoteFolder: v.sftpRemoteFolder ?? undefined,
      destinationId: id ?? undefined,
    }).subscribe({
      next: res => {
        console.info(`[SFTP Test Connection] result connected=${res.connected}${res.connected ? '' : ` — ${res.error ?? 'no error message'}`}`);
        this.probeState.set(res.connected ? 'ok' : 'error');
        if (!res.connected) this.probeError.set(res.error ?? 'Connection failed.');
      },
      error: err => {
        console.error(`[SFTP Test Connection] FAILED for destinationId=${id ?? '(none)'}`, err);
        this.probeState.set('error');
        this.probeError.set(typeof err?.error?.title === 'string' ? err.error.title : (err?.message ?? 'Connection failed.'));
      },
    });
  }

  getFullConfig(): Record<string, string> {
    const v = this.sftpForm.value;
    return {
      dest_name: v.name ?? '',
      dest_sftpHost: v.sftpHost ?? '',
      dest_sftpPort: String(v.sftpPort ?? 22),
      dest_sftpUsername: v.sftpUsername ?? '',
      dest_sftpAuthType: v.sftpAuthType ?? 'password',
      dest_sftpPassword: v.sftpPassword ?? '',
      dest_sftpRemoteFolder: v.sftpRemoteFolder ?? '',
    };
  }

  getMetadata(): { fields: Record<string, string>; secret?: string | null } | null {
    if (!this.isValid()) return null;
    const config = this.getFullConfig();
    // buildSftpUri bakes the password into the URI even when it's blank (e.g. "sftp://user:@host/folder"),
    // which is non-empty and would overwrite a working stored secret with a broken one on a no-op re-save —
    // same guard as CsvDestinationFormComponent's sftp branch / WorkflowBuildAssemblerService's isSftp branch.
    const secret =
      this.reusingExisting() && !config['dest_sftpPassword'] ? null : buildSftpUri(config);
    return {
      fields: JSON.parse(buildConnectionMetadata(config, 'csv')) as Record<string, string>,
      secret,
    };
  }

  patchFrom(fields: Record<string, string>, target?: string | null): void {
    this.sftpForm.patchValue({
      name: fields['dest_name'] || this.sftpForm.value.name || 'SFTP Destination',
      sftpHost: fields['dest_sftpHost'] || '',
      sftpPort: fields['dest_sftpPort'] ? Number(fields['dest_sftpPort']) : 22,
      sftpUsername: fields['dest_sftpUsername'] || '',
      sftpAuthType: fields['dest_sftpAuthType'] || 'password',
      sftpPassword: '',
      sftpRemoteFolder: fields['dest_sftpRemoteFolder'] || target || '',
    });
  }

  reset(): void {
    this.sftpForm.reset({
      name: 'SFTP Destination', sftpHost: '', sftpPort: 22, sftpUsername: '', sftpAuthType: 'password',
      sftpPassword: '', sftpRemoteFolder: '',
    });
    this.probeState.set('idle');
    this.probeError.set(null);
  }
}
