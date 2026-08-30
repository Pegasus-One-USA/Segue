import { Component, inject, signal, computed, viewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatDialogModule } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { HttpErrorResponse } from '@angular/common/http';
import { DIALOG_DATA, DialogRef } from '../../../core/services/dialog.service';
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
import { TRANSFORMS } from '../../../data/transforms.data';
import { Transform } from '../../../models/transform.model';

export interface DestinationConnectionDialogData {
  mode: 'create' | 'edit' | 'view';
  destination?: DestinationConfigurationDto;
}

/** The DestinationTypes this admin dialog's create-flow offers, in display order. A curated safe subset of the
 *  full catalog — each has a working form here (registry-routed, or the hand-rolled FHIR form) and a real
 *  backend writer. Analytics/File/Delivery types (Snowflake/Parquet/…) aren't offered yet: their standalone
 *  forms aren't verified in this dialog. Labels/descriptions/permission all come from the TRANSFORMS catalog
 *  (transforms.data.ts), so this stays in sync with the workflow builder's Node Library. */
const CREATE_TYPES: DestinationType[] = [
  'SqlServer', 'PostgreSql', 'MySql', 'Mongo', 'BlobStorage', 'Csv', 'FhirRepository', 'Medplum',
];

/** Types whose connection secret can be replaced from the Edit flow (their form loads here). Superset of
 *  CREATE_TYPES plus AzureSql/Sftp, which have always been edit-supported by collapsing onto the
 *  SqlServer/Csv registry forms. Anything else is name/target-only on edit (see unsupportedType()). */
const EDITABLE_TYPES: ReadonlySet<DestinationType> = new Set<DestinationType>([
  ...CREATE_TYPES, 'AzureSql', 'Sftp',
]);

/** DestinationType -> its TRANSFORMS catalog entry (name, sub, permissionPrefix). */
const DEST_CATALOG = new Map<DestinationType, Transform>(
  TRANSFORMS.filter(t => t.destinationType).map(t => [t.destinationType!, t]),
);

/** The permission-code prefix the backend authorizes this type against — its own dedicated group where it has
 *  one (sqlserver/postgresql/mysql/mongo/blobstorage/csv), else the generic `sourceconnections` fallback the
 *  catalog records (Medplum, Aidbox/FhirRepository). Same prefix the Node Library gates its tiles on. */
function permissionPrefixFor(type: DestinationType): string {
  return DEST_CATALOG.get(type)?.permissionPrefix ?? type.toLowerCase();
}

/** DestinationConfiguration.target for a freshly-built connection, mirroring workflow-build-assembler's
 *  per-type target: FHIR base URL / Medplum base URL / blob container / Mongo collection / CSV file pattern.
 *  SQL-family destinations have no natural target (null), same as before. */
const TARGET_FIELD_KEYS = ['dest_baseUrl', 'dest_medplumBaseUrl', 'dest_blobContainer', 'dest_collection', 'dest_filePattern'];
function resolveTarget(fields: Record<string, string>): string | null {
  for (const key of TARGET_FIELD_KEYS) {
    if (fields[key]) return fields[key];
  }
  return null;
}

/** Create-permission codes across every type this dialog's create-flow offers — the "can create ANY
 *  destination" gate for the list screen's New Connection button. Deduped (Medplum + Aidbox both map to
 *  sourceconnections.create). */
export const DESTINATION_CREATE_PERMISSION_CODES: string[] = [
  ...new Set(CREATE_TYPES.map(type => `${permissionPrefixFor(type)}.create`)),
];

@Component({
  selector: 'app-destination-connection-dialog',
  standalone: true,
  imports: [
    CommonModule, ReactiveFormsModule, MatDialogModule, MatButtonModule, MatIconModule,
    MatFormFieldModule, MatInputModule, MatProgressSpinnerModule, DestinationConnectionFormComponent,
  ],
  templateUrl: './destination-connection-dialog.component.html',
  styleUrl: './destination-connection-dialog.component.scss',
})
export class DestinationConnectionDialogComponent {
  private readonly fb = inject(FormBuilder);
  private readonly svc = inject(DestinationConfigurationService);
  readonly dialogRef = inject<DialogRef<DestinationConfigurationDto | false>>(DialogRef);
  private readonly actionGuard = inject(PermissionActionGuard);
  readonly permissions = inject(PermissionService);
  readonly data = inject<DestinationConnectionDialogData>(DIALOG_DATA) as DestinationConnectionDialogData;

  readonly connectionForm = viewChild(DestinationConnectionFormComponent);

  readonly mode = this.data.mode;
  readonly isCreate = this.mode === 'create';
  readonly isView = this.mode === 'view';
  readonly isEdit = this.mode === 'edit';

  // Create: the user picks a type before the rich connection form appears. Edit/view: the type is fixed,
  // derived from the saved destination — this screen only lets you *replace* an existing secret, not retype it.
  // chosenType holds the real DestinationType directly (create: the picked card; edit: the destination's own
  // type when it's editable here, else null → name/target-only).
  readonly chosenType = signal<DestinationType | null>(
    this.isCreate
      ? null
      : (EDITABLE_TYPES.has(this.data.destination!.destinationType) ? this.data.destination!.destinationType : null),
  );
  readonly unsupportedType = computed(() => !this.isCreate && this.chosenType() === null);

  /** The DestinationType handed to DestinationConnectionFormComponent's destType input — the real type in both
   *  modes (create: the chosen card; edit: the destination's own type), so "Replace connection secret" loads
   *  the matching engine's component (e.g. MySql, not SqlServer). */
  readonly formDestinationType = computed<DestinationType | null>(() => this.chosenType());

  /** Create-flow type cards, derived from the TRANSFORMS catalog (single source of truth shared with the
   *  workflow builder) and filtered to the types the current user can actually create. */
  readonly createTypeCards = CREATE_TYPES
    .map(type => DEST_CATALOG.get(type))
    .filter((t): t is Transform => !!t)
    .map(t => ({
      destinationType: t.destinationType!,
      title: t.name,
      description: t.sub,
      permissionCode: `${permissionPrefixFor(t.destinationType!)}.create`,
    }))
    .filter(card => this.permissions.hasPermission(card.permissionCode));

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

  chooseType(type: DestinationType): void {
    this.chosenType.set(type);
  }

  /** FHIR credentials aren't validated server-side the way SQL's are checked against a real schema fetch before
   *  Save is even possible — a wrong secret would otherwise save silently and only fail later at run time. Only
   *  relevant when a new secret is actually being entered: create, or edit with "Replace connection secret" on. */
  fhirTestPending(): boolean {
    if (this.chosenType() !== 'FhirRepository') return false;
    if (!this.isCreate && !this.replaceSecret()) return false;
    return this.connectionForm()?.probeState() !== 'ok';
  }

  toggleReplaceSecret(): void {
    this.replaceSecret.update(v => !v);
  }

  // View mode disables metaForm outright (never dirty) and there's no type-picker step to have
  // progressed past, so this is always false there — no confirm needed to "close" a read-only view.
  // Create mode also counts having picked a connection type as unsaved progress: the rich per-type
  // fields below it (Host/Database/Username/...) aren't individually dirty-tracked today, but a user
  // who's chosen SQL Server and started filling it in has real progress worth confirming before losing.
  hasUnsavedChanges(): boolean {
    return this.metaForm.dirty || (this.isCreate && this.chosenType() !== null);
  }

  isSaveInProgress(): boolean {
    return this.saving();
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
    if (!this.actionGuard.ensure(`${permissionPrefixFor(type)}.create`, `You do not have permission to create a ${type} destination.`)) return;

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
      // Per-type target, mirroring workflow-build-assembler (FHIR/Medplum base URL, blob container, Mongo
      // collection, CSV file pattern); SQL-family has none. Also satisfies
      // CreateDestinationConfigurationRequestValidator.ValidateFhirRepositoryMetadata, which needs a target
      // (dest_baseUrl) whenever a FhirRepository's auth type isn't 'none'.
      target: resolveTarget(metadata.fields),
      inlineSecret: metadata.secret ?? '',
      connectionMetadataJson: JSON.stringify(metadata.fields),
    };

    this._submit(() => this.svc.create(request));
  }

  private _saveEdit(): void {
    const destination = this.data.destination!;
    // Defense-in-depth: DestinationConnectionListComponent.openEdit() already checked this before
    // opening the dialog — re-checked here against a permission change landing mid-edit.
    if (!this.actionGuard.ensure(`${permissionPrefixFor(destination.destinationType)}.edit`, `You do not have permission to edit this ${destination.destinationType} destination.`)) return;
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
