import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { buildConnectionMetadata } from '../../../../destination-connections/utils/destination-connection-secret.util';
import { PhaseConfigService } from '../../../../services/phase-config.service';
import { WizardDestinationFormApi } from './destination-form-api';

/**
 * Microsoft Fabric destination — lands mapped records as files in a Lakehouse's Files area over OneLake.
 *
 * Two things this form deliberately does not offer, because the backend cannot honor them:
 * - **Auth beyond Entra.** OneLake accepts only Entra tokens: no account keys, no SAS. So where the Blob Storage
 *   form has five auth modes, this has two (managed identity / service principal).
 * - **A Tables/ path.** A Fabric Lakehouse table is a Delta table, defined by its _delta_log. This destination
 *   writes plain NDJSON/CSV/Parquet, so files dropped in Tables/ would never register as a table — the path is
 *   rejected inline here and again server-side (FabricDestinationSettings.NormalizeBasePath).
 */
@Component({
  selector: 'app-data-fabric-destination-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './data-fabric-destination-form.component.html',
  styleUrls: ['../destination-wizard.component.scss'],
})
export class DataFabricDestinationFormComponent implements WizardDestinationFormApi {
  private readonly fb = inject(FormBuilder);
  private readonly phase = inject(PhaseConfigService);

  /** Landing modes this phase offers. Warehouse stays out until a real Fabric write has been confirmed. */
  readonly availableModes = computed(() =>
    ([{ value: 'oneLakeFiles', label: 'Lakehouse files (OneLake)' },
      { value: 'warehouseTable', label: 'Warehouse table (COPY INTO)' }])
      .filter(mode => this.phase.isFabricModeEnabled(mode.value)));

  /** Only worth showing the selector when there is a choice to make. */
  readonly showModeSelector = computed(() => this.availableModes().length > 1);

  /** Whether the Advanced disclosure is expanded. Collapsed by default — every control inside it has a
   *  working default, so a first-time connection never needs to open it. */
  readonly advancedOpen = signal(false);

  toggleAdvanced(): void {
    this.advancedOpen.update(open => !open);
  }

  /** Controls inside the Advanced disclosure. Listed once so the template force-open check stays in step. */
  private static readonly ADVANCED_CONTROLS = [
    'path', 'fileFormat', 'partitionBy', 'endpointSuffix', 'authorityHost', 'accountUrl',
    'warehouseSchema', 'warehouseStagingPath',
  ];

  /**
   * True when an Advanced control is invalid and touched — the template then force-opens the section.
   * Without this, a pattern violation on Path would block Save from inside a collapsed panel, with the
   * offending field and its message both hidden and nothing on screen to explain why.
   */
  hasAdvancedError(): boolean {
    return DataFabricDestinationFormComponent.ADVANCED_CONTROLS.some(name => {
      const control = this.fabricForm.get(name);
      return !!control && control.invalid && control.touched;
    });
  }

  /** True when the Warehouse landing mode is selected — drives which fields are required and shown. */
  readonly isWarehouse = computed(() => this.modeValue() === 'warehouseTable');

  /** Mode as a signal so computed()s above react to it (valueChanges keeps it in step). */
  private readonly modeValue = signal<string>('oneLakeFiles');

  /** A workspace/item name is permissive (spaces and mixed case are normal in Fabric); only a path separator or
   *  a control character is refused, since either would silently retarget the write at a different item. */
  // eslint-disable-next-line no-control-regex -- matching control characters is precisely what this pattern is for.
  static readonly FABRIC_NAME_PATTERN = /^[^/\\\x00-\x1F\x7F]+$/;

  /** Relative path under the item's Files area. Rejects a full URL, a Tables/ path (with or without a leading
   *  "Files/"), a path repeating the item name, and "."/".." traversal — mirroring the server-side rules. */
  static readonly FABRIC_PATH_PATTERN =
    /^(?!.*:\/\/)(?!\/*(Files\/)?Tables(\/|$))(?!.*\.(Lakehouse|Warehouse))(?!.*(^|\/)\.\.?(\/|$)).*$/i;

  readonly fabricForm = this.fb.group({
    name: ['Microsoft Fabric Lakehouse', [Validators.required]],
    workspace: ['', [Validators.required, Validators.pattern(DataFabricDestinationFormComponent.FABRIC_NAME_PATTERN)]],
    itemName: ['', [Validators.required, Validators.pattern(DataFabricDestinationFormComponent.FABRIC_NAME_PATTERN)]],
    itemType: ['Lakehouse', [Validators.required]],
    // Blank means the writer's own default ("fhirbridge").
    path: ['fhirbridge', [Validators.pattern(DataFabricDestinationFormComponent.FABRIC_PATH_PATTERN)]],
    fileFormat: ['ndjson', [Validators.required]],
    partitionBy: ['resourceType', [Validators.required]],
    authMode: ['managedIdentity', [Validators.required]],
    // Only the service-principal client secret — managed identity resolves no Key Vault secret at all.
    secretValue: ['', []],
    tenantId: ['', []],
    clientId: ['', []],
    managedIdentityClientId: ['', []],
    endpointSuffix: ['fabric.microsoft.com', []],
    authorityHost: ['', []],
    // Points the writer at a plain ADLS Gen2 / Blob account instead of OneLake. Blank (the normal case)
    // means the URL is derived as https://onelake.blob.{endpointSuffix}. Set it to rehearse the whole
    // writer against an ordinary storage account before a Fabric tenant is available — the path layout,
    // partitioning, serialization and Entra auth are all identical; only the host differs.
    accountUrl: ['', []],

    // ---- Warehouse landing mode only ----
    mode: ['oneLakeFiles', [Validators.required]],
    /** TDS connection string from the Warehouse's settings in Fabric. A different endpoint from OneLake,
     *  so it cannot be derived from the workspace. */
    warehouseSqlEndpoint: ['', []],
    /** A Warehouse has no Files area, so COPY INTO stages through a Lakehouse in the same workspace. */
    warehouseStagingLakehouse: ['', []],
    warehouseTable: ['', []],
    warehouseSchema: ['dbo', []],
    warehouseWriteMode: ['append', []],
    warehouseStagingPath: ['_staging', []],
  });

  /** True while the host is reusing a previously-saved connection unchanged — the client secret is never
   *  repopulated when patching from an existing connection, so requiring it would block reuse. */
  readonly reusingExisting = input<boolean>(false);

  constructor() {
    effect(() => {
      const authMode = this.fabricForm.controls.authMode.value;
      const reusing = this.reusingExisting();
      untracked(() => this._syncAuthModeValidators(authMode, reusing));
    });
    this.fabricForm.controls.authMode.valueChanges.subscribe(v =>
      this._syncAuthModeValidators(v, this.reusingExisting()));

    // Mode drives both a signal (for the computed()s) and its own required-field set.
    this.fabricForm.controls.mode.valueChanges.subscribe(mode => {
      this.modeValue.set(mode ?? 'oneLakeFiles');
      this._syncModeValidators(mode);
    });
  }

  /**
   * Mirrors FabricDestinationSettings.Parse: Warehouse mode needs a SQL endpoint and a staging lakehouse,
   * neither of which can be defaulted. Item type is forced to match the mode, because a Warehouse COPY INTO
   * aimed at a Lakehouse cannot work - a Lakehouse's SQL analytics endpoint is read-only.
   */
  private _syncModeValidators(mode: string | null): void {
    const isWarehouse = mode === 'warehouseTable';

    for (const name of ['warehouseSqlEndpoint', 'warehouseStagingLakehouse']) {
      const control = this.fabricForm.get(name)!;
      control.setValidators(isWarehouse ? [Validators.required] : []);
      control.updateValueAndValidity({ emitEvent: false });
    }

    this.fabricForm.controls.itemType.setValue(
      isWarehouse ? 'Warehouse' : 'Lakehouse', { emitEvent: false });
  }

  /** Mirrors FabricDestinationSettings.Parse: service principal needs tenant id, client id and a secret;
   *  managed identity needs none of the three. */
  private _syncAuthModeValidators(authMode: string | null, reusingExisting: boolean): void {
    const isServicePrincipal = authMode === 'servicePrincipal';

    const secretCtrl = this.fabricForm.get('secretValue')!;
    secretCtrl.setValidators(isServicePrincipal && !reusingExisting ? [Validators.required] : []);
    secretCtrl.updateValueAndValidity({ emitEvent: false });

    const tenantCtrl = this.fabricForm.get('tenantId')!;
    tenantCtrl.setValidators(isServicePrincipal ? [Validators.required] : []);
    tenantCtrl.updateValueAndValidity({ emitEvent: false });

    const clientCtrl = this.fabricForm.get('clientId')!;
    clientCtrl.setValidators(isServicePrincipal ? [Validators.required] : []);
    clientCtrl.updateValueAndValidity({ emitEvent: false });
  }

  /** The OneLake path the current settings will write under — shown back to the user, since the workspace/item/
   *  path/partition fields combine into something non-obvious. */
  resolvedPathPreview(): string {
    const v = this.fabricForm.value;
    const item = `${v.itemName || '<lakehouse>'}.${v.itemType || 'Lakehouse'}`;
    const base = (v.path || 'fhirbridge').replace(/^\/+|\/+$/g, '');
    const partitions =
      v.partitionBy === 'resourceType' ? '/resourceType=Patient'
      : v.partitionBy === 'ingestDate' ? '/ingest_date=2026-09-08'
      : v.partitionBy === 'resourceTypeAndIngestDate' ? '/resourceType=Patient/ingest_date=2026-09-08'
      : '';
    const extension = v.fileFormat === 'parquet' ? 'parquet' : v.fileFormat === 'csv' ? 'csv' : 'ndjson';
    return `${v.workspace || '<workspace>'}/${item}/Files/${base}${partitions}/Patient_<timestamp>.${extension}`;
  }

  isValid(): boolean {
    return this.fabricForm.valid;
  }

  getRawValue(): Record<string, unknown> {
    return this.fabricForm.getRawValue();
  }

  getFullConfig(): Record<string, string> {
    const v = this.fabricForm.value;
    return {
      dest_name: v.name ?? '',
      // Eventstream is never offered: it is authenticated HTTP, already served by the Data Lake Webhook
      // destination, and the server redirects there by name. Which of the remaining modes appear is
      // phase-gated (see PhaseConfig.enabledFabricModes).
      dest_fabricMode: v.mode ?? 'oneLakeFiles',
      dest_fabricWorkspace: v.workspace ?? '',
      dest_fabricItemName: v.itemName ?? '',
      dest_fabricItemType: v.itemType ?? 'Lakehouse',
      dest_fabricPath: v.path ?? '',
      dest_fabricFileFormat: v.fileFormat ?? 'ndjson',
      dest_fabricPartitionBy: v.partitionBy ?? 'resourceType',
      dest_fabricAuthMode: v.authMode ?? 'managedIdentity',
      dest_fabricSecret: v.secretValue ?? '',
      dest_fabricTenantId: v.tenantId ?? '',
      dest_fabricClientId: v.clientId ?? '',
      dest_fabricManagedIdentityClientId: v.managedIdentityClientId ?? '',
      dest_fabricEndpointSuffix: v.endpointSuffix ?? 'fabric.microsoft.com',
      dest_fabricAuthorityHost: v.authorityHost ?? '',
      dest_fabricAccountUrl: v.accountUrl ?? '',
      dest_fabricWarehouseSqlEndpoint: v.warehouseSqlEndpoint ?? '',
      dest_fabricWarehouseStagingLakehouse: v.warehouseStagingLakehouse ?? '',
      dest_fabricWarehouseTable: v.warehouseTable ?? '',
      dest_fabricWarehouseSchema: v.warehouseSchema ?? 'dbo',
      dest_fabricWarehouseWriteMode: v.warehouseWriteMode ?? 'append',
      dest_fabricWarehouseStagingPath: v.warehouseStagingPath ?? '_staging',
    };
  }

  getMetadata(): { fields: Record<string, string>; secret?: string | null } | null {
    if (!this.isValid()) return null;
    const config = this.getFullConfig();
    return {
      fields: JSON.parse(buildConnectionMetadata(config, 'fabric')) as Record<string, string>,
      // Managed identity never resolves a Key Vault secret (see FabricDestinationSettings.RequiresSecret).
      secret: config['dest_fabricAuthMode'] === 'servicePrincipal' ? (config['dest_fabricSecret'] || '') : '',
    };
  }

  patchFrom(fields: Record<string, string>, target?: string | null): void {
    this.fabricForm.patchValue({
      name: fields['dest_name'] || this.fabricForm.value.name || 'Microsoft Fabric Lakehouse',
      workspace: fields['dest_fabricWorkspace'] || target || '',
      itemName: fields['dest_fabricItemName'] || '',
      itemType: fields['dest_fabricItemType'] || 'Lakehouse',
      path: fields['dest_fabricPath'] ?? 'fhirbridge',
      fileFormat: fields['dest_fabricFileFormat'] || 'ndjson',
      partitionBy: fields['dest_fabricPartitionBy'] || 'resourceType',
      authMode: fields['dest_fabricAuthMode'] || 'managedIdentity',
      secretValue: '',
      tenantId: fields['dest_fabricTenantId'] || '',
      clientId: fields['dest_fabricClientId'] || '',
      managedIdentityClientId: fields['dest_fabricManagedIdentityClientId'] || '',
      endpointSuffix: fields['dest_fabricEndpointSuffix'] || 'fabric.microsoft.com',
      authorityHost: fields['dest_fabricAuthorityHost'] || '',
      accountUrl: fields['dest_fabricAccountUrl'] || '',
      mode: fields['dest_fabricMode'] || 'oneLakeFiles',
      warehouseSqlEndpoint: fields['dest_fabricWarehouseSqlEndpoint'] || '',
      warehouseStagingLakehouse: fields['dest_fabricWarehouseStagingLakehouse'] || '',
      warehouseTable: fields['dest_fabricWarehouseTable'] || '',
      warehouseSchema: fields['dest_fabricWarehouseSchema'] || 'dbo',
      warehouseWriteMode: fields['dest_fabricWarehouseWriteMode'] || 'append',
      warehouseStagingPath: fields['dest_fabricWarehouseStagingPath'] || '_staging',
    });
    this._syncAuthModeValidators(this.fabricForm.value.authMode ?? null, this.reusingExisting());
    this.modeValue.set(this.fabricForm.value.mode ?? 'oneLakeFiles');
    this._syncModeValidators(this.fabricForm.value.mode ?? null);
  }

  reset(): void {
    this.fabricForm.reset({
      name: 'Microsoft Fabric Lakehouse', workspace: '', itemName: '', itemType: 'Lakehouse',
      path: 'fhirbridge', fileFormat: 'ndjson', partitionBy: 'resourceType',
      authMode: 'managedIdentity', secretValue: '', tenantId: '', clientId: '',
      managedIdentityClientId: '', endpointSuffix: 'fabric.microsoft.com', authorityHost: '',
      accountUrl: '', mode: 'oneLakeFiles', warehouseSqlEndpoint: '',
      warehouseStagingLakehouse: '', warehouseTable: '', warehouseSchema: 'dbo',
      warehouseWriteMode: 'append', warehouseStagingPath: '_staging',
    });
    this._syncAuthModeValidators(this.fabricForm.value.authMode ?? null, this.reusingExisting());
    this.modeValue.set('oneLakeFiles');
    this._syncModeValidators('oneLakeFiles');
    this.advancedOpen.set(false);
  }
}
