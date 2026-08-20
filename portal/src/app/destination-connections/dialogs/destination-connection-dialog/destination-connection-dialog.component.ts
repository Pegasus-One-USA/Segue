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
import { newSecretName } from '../../utils/destination-connection-secret.util';
import { PermissionActionGuard } from '../../../auth/services/permission-action-guard.service';
import { PermissionService } from '../../../auth/services/permission.service';

export interface DestinationConnectionDialogData {
  mode: 'create' | 'edit' | 'view';
  destination?: DestinationConfigurationDto;
}

/** Still only offers Sql/Csv/Fhir from this screen's create-flow type picker (three cards) — unchanged UX.
 *  'sql' now resolves to the real SqlServer DestinationType (the registry's default SQL-family entry point),
 *  since DESTINATION_FORM_REGISTRY components no longer have an in-form "Database engine" dropdown to pick
 *  MySQL/PostgreSQL/AzureSql from (see SqlFamilyDestinationFormComponent) — creating those specific engines
 *  isn't reachable from this admin dialog yet, only from a future full registry-driven type picker. */
function chosenTypeToDestinationType(t: 'sql' | 'csv' | 'fhir'): DestinationType {
  return t === 'sql' ? 'SqlServer' : t === 'fhir' ? 'FhirRepository' : 'Csv';
}

/** Maps the entity's full enum down to the three form shapes this screen's create-flow choice cards offer,
 *  for `unsupportedType()`'s edit-mode gating only — unrelated to which exact registry component
 *  DestinationConnectionFormComponent loads for editing (see toEditableDestinationType below), which now
 *  uses the destination's real, un-collapsed DestinationType instead. */
function toFormType(t: DestinationType): 'sql' | 'csv' | 'fhir' | null {
  if (t === 'SqlServer' || t === 'AzureSql' || t === 'PostgreSql' || t === 'MySql') return 'sql';
  if (t === 'Csv' || t === 'Sftp') return 'csv';
  if (t === 'FhirRepository') return 'fhir';
  return null;
}

/** The real DestinationType to load into DestinationConnectionFormComponent for "Replace connection secret"
 *  on an existing SQL-family/CSV-family destination — the un-collapsed type (so e.g. editing a MySql
 *  destination loads MySqlDestinationFormComponent, not SqlServerDestinationFormComponent), gated by the same
 *  family membership toFormType() already checks (unsupportedType() covers everything else). */
function toEditableDestinationType(t: DestinationType): DestinationType | null {
  return toFormType(t) ? t : null;
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
  private readonly actionGuard = inject(PermissionActionGuard);
  readonly permissions = inject(PermissionService);
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

  /** The real DestinationType handed to DestinationConnectionFormComponent's (now-widened) destType input —
   *  distinct from chosenType() above, which stays the UI-facing 'sql'/'csv' shorthand the two create-flow
   *  cards and unsupportedType()'s edit-mode gating use. Create always resolves 'sql' to SqlServer (see
   *  chosenTypeToDestinationType); edit/view use the destination's own real, un-collapsed type so "Replace
   *  connection secret" loads the matching engine's component (e.g. MySql, not SqlServer). */
  readonly formDestinationType = computed<DestinationType | null>(() =>
    this.isCreate
      ? (this.chosenType() ? chosenTypeToDestinationType(this.chosenType()!) : null)
      : toEditableDestinationType(this.data.destination!.destinationType),
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
    const type = this.formDestinationType();
    const form = this.connectionForm();
    if (!type || !form) return;
    // Defense-in-depth: the trigger (DestinationConnectionListComponent.openNew()) already checked
    // "holds any create code" before this dialog opened — this re-checks against the SPECIFIC type
    // the user just picked from the two type-choice cards, since holding sqlserver.create doesn't
    // imply csv.create or vice versa.
    if (!this.actionGuard.ensure(`${type.toLowerCase()}.create`, `You do not have permission to create a ${type} destination.`)) return;

    const metadata = form.getMetadata();
    if (!metadata) {
      this.errorMessage.set('Fix the highlighted fields before saving.');
      return;
    }

    const name = metadata.fields['dest_name'] || 'New Destination';
    const request: CreateDestinationConfigurationRequest = {
      name,
      destinationType: type,
      keyVaultName: 'workflow-secrets',
      secretName: newSecretName(name),
      // Required by CreateDestinationConfigurationRequestValidator.ValidateFhirRepositoryMetadata whenever
      // dest_fhirAuthType isn't 'none' (the FHIR form always sets an auth type, so effectively always
      // required for FhirRepository in practice) — dest_baseUrl covers that case, dest_filePattern covers Csv.
      target: metadata.fields['dest_baseUrl'] || metadata.fields['dest_filePattern'] || null,
      inlineSecret: metadata.secret ?? '',
      connectionMetadataJson: JSON.stringify(metadata.fields),
    };

    this._submit(() => this.svc.create(request));
  }

  private _saveEdit(): void {
    const destination = this.data.destination!;
    // Defense-in-depth: DestinationConnectionListComponent.openEdit() already checked this before
    // opening the dialog — re-checked here against a permission change landing mid-edit.
    if (!this.actionGuard.ensure(`${destination.destinationType.toLowerCase()}.edit`, `You do not have permission to edit this ${destination.destinationType} destination.`)) return;
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

    if (this.replaceSecret() && this.formDestinationType()) {
      const form = this.connectionForm();
      const metadata = form?.getMetadata();
      if (!metadata) {
        this.errorMessage.set('Fix the highlighted connection fields before saving.');
        return;
      }
      request.inlineSecret = metadata.secret ?? '';
      request.connectionMetadataJson = JSON.stringify(metadata.fields);
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
