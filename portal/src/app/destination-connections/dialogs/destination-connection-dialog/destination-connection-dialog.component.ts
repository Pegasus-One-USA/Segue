import { Component, inject, signal, computed, viewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogRef, MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { HttpErrorResponse } from '@angular/common/http';
import { DestinationConnectionFormComponent } from '../../../components/node-library/destination-wizard/destination-connection-form.component';
import { DestinationConfigurationService } from '../../services/destination-configuration.service';
import {
  CreateDestinationConfigurationRequest,
  DestinationConfigurationDto,
  DestinationType,
} from '../../models/destination-configuration.model';
import { buildConnectionMetadata, buildFhirSecretBlob, buildSftpUri, buildSqlConnectionString, newSecretName } from '../../utils/destination-connection-secret.util';

export interface DestinationConnectionDialogData {
  mode: 'create' | 'edit' | 'view';
  destination?: DestinationConfigurationDto;
}

/** Maps the entity's full 21-value enum down to the three form shapes this screen (and the workflow wizard) support. */
function toFormType(t: DestinationType): 'sql' | 'csv' | 'fhir' | null {
  if (t === 'SqlServer' || t === 'AzureSql' || t === 'PostgreSql' || t === 'MySql') return 'sql';
  if (t === 'Csv' || t === 'Sftp') return 'csv';
  if (t === 'FhirRepository') return 'fhir';
  return null;
}

/** The connection form's engine dropdown value for a saved destination's real DestinationType — used to
 *  pre-select the right engine when editing/replacing the secret on an existing SQL-family destination. */
function toEngine(t: DestinationType): 'sqlserver' | 'mysql' | 'postgres' {
  if (t === 'PostgreSql') return 'postgres';
  if (t === 'MySql') return 'mysql';
  return 'sqlserver';
}

/** Reverse of toEngine — resolves the connection form's chosen engine back to the DestinationType to save. */
function sqlEngineToDestinationType(engine: string | undefined): DestinationType {
  if (engine === 'postgres') return 'PostgreSql';
  if (engine === 'mysql') return 'MySql';
  return 'SqlServer';
}

@Component({
  selector: 'app-destination-connection-dialog',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule, MatDialogModule, MatButtonModule, DestinationConnectionFormComponent],
  templateUrl: './destination-connection-dialog.component.html',
  styleUrl: './destination-connection-dialog.component.scss',
})
export class DestinationConnectionDialogComponent {
  private readonly fb = inject(FormBuilder);
  private readonly svc = inject(DestinationConfigurationService);
  private readonly dialogRef = inject(MatDialogRef<DestinationConnectionDialogComponent>);
  readonly data = inject<DestinationConnectionDialogData>(MAT_DIALOG_DATA);

  readonly connectionForm = viewChild(DestinationConnectionFormComponent);

  readonly mode = this.data.mode;
  readonly isCreate = this.mode === 'create';
  readonly isView = this.mode === 'view';
  readonly isEdit = this.mode === 'edit';

  // Create: the user picks a type before the rich connection form appears. Edit/view: the type is fixed,
  // derived from the saved destination — this screen only lets you *replace* an existing secret, not retype it.
  readonly chosenType = signal<'sql' | 'csv' | 'fhir' | null>(
    this.isCreate ? null : toFormType(this.data.destination!.destinationType),
  );
  readonly unsupportedType = computed(() => !this.isCreate && this.chosenType() === null);

  // Edit/view only: pre-selects the connection form's engine dropdown (SQL Server/MySQL/PostgreSQL) to match
  // the destination being edited, so "Replace connection secret" doesn't silently default back to SQL Server.
  readonly editInitialConfig = computed<Record<string, string> | null>(() =>
    !this.isCreate && this.data.destination
      ? { dest_engine: toEngine(this.data.destination.destinationType) }
      : null,
  );

  // Edit/view: Name + Target map straight to DestinationConfiguration's own persisted fields. The rich
  // sql/csv connection fields (server, credentials, folder, etc.) are never returned by the API — they were
  // folded into an opaque, write-only secret at creation — so they can't be pre-filled; "Replace connection
  // secret" below lets an editor supply fresh ones rather than pretending stale/blank values are current.
  readonly metaForm = this.fb.group({
    name: [this.data.destination?.name ?? '', [Validators.required]],
    target: [this.data.destination?.target ?? ''],
  });

  readonly replaceSecret = signal(false);
  readonly saving = signal(false);
  readonly errorMessage = signal<string | null>(null);

  constructor() {
    if (this.isView) {
      this.metaForm.disable({ emitEvent: false });
    }
  }

  chooseType(type: 'sql' | 'csv' | 'fhir'): void {
    this.chosenType.set(type);
  }

  /** FHIR credentials aren't validated server-side the way SQL's are checked against a real schema fetch before
   *  Save is even possible — a wrong secret would otherwise save silently and only fail later at run time. Only
   *  relevant when a new secret is actually being entered: create, or edit with "Replace connection secret" on. */
  fhirTestPending(): boolean {
    if (this.chosenType() !== 'fhir') return false;
    if (!this.isCreate && !this.replaceSecret()) return false;
    return this.connectionForm()?.probeState() !== 'ok';
  }

  toggleReplaceSecret(): void {
    this.replaceSecret.update(v => !v);
  }

  cancel(): void {
    this.dialogRef.close(false);
  }

  save(): void {
    this.errorMessage.set(null);

    if (this.isCreate) {
      this._saveCreate();
    } else {
      this._saveEdit();
    }
  }

  private _saveCreate(): void {
    const type = this.chosenType();
    const form = this.connectionForm();
    if (!type || !form) return;

    const config = form.getConfig();
    if (!config) {
      this.errorMessage.set('Fix the highlighted fields before saving.');
      return;
    }

    const name = config['dest_name'] || 'New Destination';
    const request: CreateDestinationConfigurationRequest =
      type === 'sql'
        ? {
            name,
            destinationType: sqlEngineToDestinationType(config['dest_engine']),
            keyVaultName: 'workflow-secrets',
            secretName: newSecretName(name),
            target: null,
            inlineSecret: buildSqlConnectionString(config),
            connectionMetadataJson: buildConnectionMetadata(config, 'sql'),
          }
        : type === 'fhir'
          ? {
              name,
              destinationType: 'FhirRepository',
              keyVaultName: 'workflow-secrets',
              secretName: newSecretName(name),
              // Required by CreateDestinationConfigurationRequestValidator.ValidateFhirRepositoryMetadata
              // whenever dest_fhirAuthType isn't 'none' — the form always sets an auth type, so this is
              // effectively always required in practice.
              target: config['dest_baseUrl'] || null,
              inlineSecret: buildFhirSecretBlob(config),
              connectionMetadataJson: buildConnectionMetadata(config, 'fhir'),
            }
          : {
              name,
              // Always 'Csv': the delivery mode (download/email/sftp/download-link) is a ConnectionMetadataJson
              // field (dest_deliveryMode), not the DestinationType — a single writer dispatches on it internally.
              destinationType: 'Csv',
              keyVaultName: 'workflow-secrets',
              secretName: newSecretName(name),
              target: config['dest_filePattern'] || null,
              // Only SFTP delivery actually reads this secret; the other three modes never resolve it, so any
              // placeholder value is fine there.
              inlineSecret: config['dest_deliveryMode'] === 'sftp' ? buildSftpUri(config) : '',
              connectionMetadataJson: buildConnectionMetadata(config, 'csv'),
            };

    this._submit(() => this.svc.create(request));
  }

  private _saveEdit(): void {
    const destination = this.data.destination!;
    if (this.metaForm.invalid) {
      this.metaForm.markAllAsTouched();
      return;
    }

    const request: CreateDestinationConfigurationRequest = {
      name: this.metaForm.value.name!,
      destinationType: destination.destinationType,
      keyVaultName: destination.keyVaultName,
      secretName: destination.secretName,
      target: this.metaForm.value.target || null,
    };

    if (this.replaceSecret() && this.chosenType()) {
      const form = this.connectionForm();
      const config = form?.getConfig();
      if (!config) {
        this.errorMessage.set('Fix the highlighted connection fields before saving.');
        return;
      }
      const type = this.chosenType();
      request.inlineSecret =
        type === 'sql'
          ? buildSqlConnectionString(config)
          : type === 'fhir'
            ? buildFhirSecretBlob(config)
            : config['dest_deliveryMode'] === 'sftp'
              ? buildSftpUri(config)
              : '';
      request.connectionMetadataJson = buildConnectionMetadata(config, type === 'sql' ? 'sql' : type === 'fhir' ? 'fhir' : 'csv');
    }

    this._submit(() => this.svc.update(destination.id, request));
  }

  private _submit(action: () => ReturnType<DestinationConfigurationService['create']>): void {
    this.saving.set(true);
    action().subscribe({
      next: result => {
        this.saving.set(false);
        this.dialogRef.close(result);
      },
      error: (err: HttpErrorResponse) => {
        this.saving.set(false);
        this.errorMessage.set(err.error?.title ?? 'Failed to save the destination connection.');
      },
    });
  }
}
