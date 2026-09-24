import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { buildConnectionMetadata } from '../../../../destination-connections/utils/destination-connection-secret.util';
import { PhaseConfigServiceV2 } from '../../../../services/phase-config-v2.service';
import { DestinationSchemaService, DestinationTable, DestinationProbeRequest } from '../../../../services/destination-schema.service';
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
  private readonly schemaSvc = inject(DestinationSchemaService);
  private readonly phase = inject(PhaseConfigServiceV2);

  /** The concrete destination type this form instance is serving (see DestinationWizardComponent
   *  .activeFormInputs). One form backs both Fabric types, and the type is what decides the landing surface:
   *  DataFabricWarehouse IS the Warehouse surface. Defaults to the file type so any caller that does not pass
   *  it behaves exactly as before. */
  readonly destinationType = input<string | null>(null);

  /** True when this form was opened as the dedicated Warehouse destination type, rather than as the file type
   *  with a mode chosen inside it. */
  readonly isWarehouseType = computed(() => this.destinationType() === 'DataFabricWarehouse');

  /** Landing modes this phase offers. Warehouse stays out until a real Fabric write has been confirmed.
   *  When the TYPE already fixes the surface there is no choice to offer, so the list collapses to that one
   *  mode — which also makes showModeSelector() below false. */
  readonly availableModes = computed(() => {
    if (this.isWarehouseType()) {
      return [{ value: 'warehouseTable', label: 'Warehouse table (COPY INTO)' }];
    }
    return ([{ value: 'oneLakeFiles', label: 'Lakehouse files (OneLake)' },
      { value: 'warehouseTable', label: 'Warehouse table (COPY INTO)' }])
      .filter(mode => this.phase.isFabricModeEnabled(mode.value));
  });

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
    /** COPY INTO runs inside the Warehouse, which reads the staging file with its OWN identity rather than
     *  the one that authenticated this connection. With no credential it authenticates as nobody. */
    warehouseUseWorkspaceIdentity: [true, []],
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

    // When the TYPE fixes the surface, the mode control follows it rather than its own default. Without this
    // the Warehouse destination opened on 'oneLakeFiles' — the form built its default before any input was
    // bound — so it collected Lakehouse fields and saved a Warehouse destination that pointed at files.
    // An effect (not a one-shot) because the input arrives after construction.
    effect(() => {
      if (!this.isWarehouseType()) return;
      untracked(() => {
        if (this.fabricForm.controls.mode.value === 'warehouseTable') return;
        this.fabricForm.controls.mode.setValue('warehouseTable');
      });
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
      dest_fabricWarehouseUseWorkspaceIdentity: v.warehouseUseWorkspaceIdentity ? 'true' : 'false',
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
      warehouseUseWorkspaceIdentity: fields['dest_fabricWarehouseUseWorkspaceIdentity'] !== 'false',
      warehouseStagingPath: fields['dest_fabricWarehouseStagingPath'] || '_staging',
    });
    this._syncAuthModeValidators(this.fabricForm.value.authMode ?? null, this.reusingExisting());
    this.modeValue.set(this.fabricForm.value.mode ?? 'oneLakeFiles');
    this._syncModeValidators(this.fabricForm.value.mode ?? null);
  }

  /** Mirrors the other destination forms' probeState/probeError pair. */
  readonly probeState = signal<'idle' | 'testing' | 'ok' | 'error'>('idle');

  /** Tables the last successful Warehouse test returned, in the same shape (and under the same name) the
   *  SQL-family forms expose — that is what lets DestinationWizardComponent hand them to the mapping canvas
   *  through its existing sqlTables() plumbing rather than a Fabric-specific path. Always empty for the
   *  OneLake Files surface, which has no tables. */
  private readonly _sqlTables = signal<DestinationTable[]>([]);
  sqlTables(): DestinationTable[] {
    return this._sqlTables();
  }

  /**
   * Ad-hoc connection details for the mapping canvas's live schema probe and its real CREATE TABLE /
   * ALTER TABLE calls — the Fabric equivalent of the SQL-family forms method of the same name.
   *
   * Carries no server/database/password because a Warehouse has none: the backend opens this with an Entra
   * token instead (see SqlDestinationSchemaService.OpenForProbeAsync). Deliberately NOT named alongside
   * getProbeRequest on the SqlFamilyFormApi interface — isSqlFamilyForm() must keep rejecting this form, or
   * the wizard would route it through the SQL probe-then-advance branch it cannot satisfy.
   */
  getFabricProbeRequest(): DestinationProbeRequest {
    const v = this.fabricForm.value;
    return {
      destinationType: 'DataFabricWarehouse',
      fabricWorkspace: v.workspace ?? '',
      fabricItemName: v.itemName ?? '',
      fabricWarehouseSqlEndpoint: v.warehouseSqlEndpoint ?? '',
      fabricAuthMode: v.authMode ?? 'managedIdentity',
      fabricTenantId: v.authMode === 'servicePrincipal' ? (v.tenantId ?? undefined) : undefined,
      fabricClientId: v.authMode === 'servicePrincipal' ? (v.clientId ?? undefined) : undefined,
      fabricSecret: v.authMode === 'servicePrincipal' ? (v.secretValue ?? undefined) : undefined,
      fabricManagedIdentityClientId:
        v.authMode === 'managedIdentity' ? (v.managedIdentityClientId ?? undefined) : undefined,
      fabricEndpointSuffix: v.endpointSuffix || undefined,
      fabricAuthorityHost: v.authorityHost || undefined,
      // Lets the backend fall back to the saved secret when the form left it blank, the same way a SQL
      // password is inherited (see DestinationConnectionProbeRequest.ExistingDestinationId).
      existingDestinationId: this.existingDestinationId() ?? undefined,
    };
  }
  readonly probeError = signal<string | null>(null);
  /** Set when the identity authenticated but was refused — surfaced separately from the raw error
   *  because it names the fix (a Fabric workspace role), which the raw 403 does not. */
  readonly probePermissionHint = signal<string | null>(null);
  /** Which half succeeded, so a Warehouse failure can say OneLake was fine. */
  readonly probeOneLakeOk = signal<boolean>(false);

  /** The saved destination id when reusing one unchanged — lets the backend resolve the stored secret,
   *  which the form never repopulates. Without this, testing a reused connection would be impossible. */
  readonly existingDestinationId = input<string | null>(null);

  /** Workspace and item are always needed; a service principal additionally needs tenant, client and
   *  either a typed secret or a stored one to fall back to. Warehouse mode also needs its endpoint. */
  canTestConnection(): boolean {
    const v = this.fabricForm.value;
    if (!v.workspace || !v.itemName) return false;
    if (this.isWarehouse() && !v.warehouseSqlEndpoint) return false;
    if (v.authMode === 'servicePrincipal') {
      return !!(v.tenantId && v.clientId
        && (v.secretValue || (this.reusingExisting() && this.existingDestinationId())));
    }
    return true;
  }

  /** Tests with the form's CURRENT (not-yet-saved) values. Backed by POST /destinations/fabric-test. */
  testConnection(): void {
    if (!this.canTestConnection()) return;
    const v = this.fabricForm.value;
    this.probeState.set('testing');
    this.probeError.set(null);
    this.probePermissionHint.set(null);
    this.probeOneLakeOk.set(false);

    this.schemaSvc
      .testFabric({
        mode: v.mode ?? 'oneLakeFiles',
        authMode: v.authMode ?? 'managedIdentity',
        workspace: v.workspace ?? '',
        itemName: v.itemName ?? '',
        itemType: v.itemType ?? undefined,
        secret: v.authMode === 'servicePrincipal' ? (v.secretValue ?? undefined) : undefined,
        tenantId: v.authMode === 'servicePrincipal' ? (v.tenantId ?? undefined) : undefined,
        clientId: v.authMode === 'servicePrincipal' ? (v.clientId ?? undefined) : undefined,
        managedIdentityClientId: v.authMode === 'managedIdentity'
          ? (v.managedIdentityClientId ?? undefined) : undefined,
        endpointSuffix: v.endpointSuffix || undefined,
        authorityHost: v.authorityHost || undefined,
        accountUrl: v.accountUrl || undefined,
        warehouseSqlEndpoint: this.isWarehouse() ? (v.warehouseSqlEndpoint ?? undefined) : undefined,
        warehouseStagingLakehouse: this.isWarehouse()
          ? (v.warehouseStagingLakehouse ?? undefined) : undefined,
        destinationId: this.reusingExisting() ? (this.existingDestinationId() ?? undefined) : undefined,
      })
      .subscribe({
        next: res => {
          this.probeOneLakeOk.set(res.oneLakeReachable);
          if (!res.connected) {
            this.probeState.set('error');
            this.probeError.set(res.error ?? 'Connection failed.');
            this.probePermissionHint.set(res.permissionHint ?? null);
            return;
          }
          // Carried straight off the probe so a not-yet-provisioned Warehouse can populate the mapping
          // canvas's table picker, exactly as the SQL-family forms do with their own probe result.
          this._sqlTables.set(res.tables ?? []);
          this.probeState.set('ok');
        },
        error: err => {
          this.probeState.set('error');
          this.probeError.set(
            typeof err?.error?.error === 'string' ? err.error.error : 'Connection failed.');
        },
      });
  }

  reset(): void {
    this.fabricForm.reset({
      name: 'Microsoft Fabric Lakehouse', workspace: '', itemName: '', itemType: 'Lakehouse',
      path: 'fhirbridge', fileFormat: 'ndjson', partitionBy: 'resourceType',
      authMode: 'managedIdentity', secretValue: '', tenantId: '', clientId: '',
      managedIdentityClientId: '', endpointSuffix: 'fabric.microsoft.com', authorityHost: '',
      accountUrl: '', mode: 'oneLakeFiles', warehouseSqlEndpoint: '',
      warehouseStagingLakehouse: '', warehouseTable: '', warehouseSchema: 'dbo',
      warehouseWriteMode: 'append', warehouseUseWorkspaceIdentity: true, warehouseStagingPath: '_staging',
    });
    this._syncAuthModeValidators(this.fabricForm.value.authMode ?? null, this.reusingExisting());
    this.modeValue.set('oneLakeFiles');
    this._syncModeValidators('oneLakeFiles');
    this.advancedOpen.set(false);
    this.probeState.set('idle');
    this.probeError.set(null);
    this.probePermissionHint.set(null);
  }
}
