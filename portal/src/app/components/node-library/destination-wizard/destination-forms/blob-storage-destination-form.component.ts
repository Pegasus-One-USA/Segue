import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { buildConnectionMetadata } from '../../../../destination-connections/utils/destination-connection-secret.util';
import { WizardDestinationFormApi } from './destination-form-api';
import { DestinationSchemaService } from '../../../../services/destination-schema.service';

/**
 * Azure Blob Storage destination — five auth modes (connection string / account key / SAS / managed identity /
 * service principal; the latter two are hidden in the template for now but the form/validation still supports
 * them), Azure container-name validation, and an independent granularity (bulk vs individual blobs) + record
 * mode (insert/upsert/update, only meaningful for individual) write-mode split.
 */
@Component({
  selector: 'app-blob-storage-destination-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './blob-storage-destination-form.component.html',
  styleUrls: ['../destination-wizard.component.scss'],
})
export class BlobStorageDestinationFormComponent implements WizardDestinationFormApi {
  private readonly fb = inject(FormBuilder);
  private readonly schemaSvc = inject(DestinationSchemaService);

  readonly probeState = signal<'idle' | 'testing' | 'ok' | 'error'>('idle');
  readonly probeError = signal<string | null>(null);

  // What Azure actually allows in a blob name: no backslash (not a supported path delimiter — "/" is), no
  // control characters, and (the second negative lookahead) not ending in "." or "/". Matches
  // BlobDestinationSettings.ValidatePattern/CreateDestinationConfigurationRequestValidator's server-side checks —
  // this is just the inline, before-you-even-save layer. Blank is valid (matches — "use the record mode's
  // default"); placeholder tokens like "{date:yyyy/MM/dd}" are unaffected, since "{" / "}" / ":" aren't restricted.
  // Folder pattern is the one place nesting belongs, so "/" is allowed here.
  static readonly BLOB_FOLDER_NAMING_PATTERN = /^(?!.*[\\\x00-\x1F\x7F])(?!.*[./]$).*$/;

  // Same rule as above, but File name pattern additionally forbids "/" ANYWHERE — a "/" there would silently
  // split the record's blob into extra folders instead of naming a single file (nesting belongs in Folder
  // pattern). Subsumes the "doesn't end with '/'" check above, since no "/" is allowed at all; still checks
  // "doesn't end with '.'" separately.
  static readonly BLOB_FILE_NAME_PATTERN = /^(?!.*[\\/\x00-\x1F\x7F])(?!.*\.$).*$/;

  readonly blobForm = this.fb.group({
    name: ['Azure Blob Export', [Validators.required]],
    // Azure container naming rules: 3-63 chars, lowercase letters/digits/hyphens, no leading/trailing/double
    // hyphens. A name violating this is rejected by Azure at write time with an opaque "InvalidResourceName"
    // error — catching it here up front avoids that round trip.
    container: ['', [Validators.required, Validators.pattern(/^(?!.*--)[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$/)]],
    authMode: ['connectionString', [Validators.required]],
    // The one secret control for every auth mode (connection string / account key / SAS / client secret) —
    // its label swaps per authMode in the template. Never repopulated when reusing an existing connection
    // (secrets never come back from the API), same as SFTP's password.
    secretValue: ['', []],
    accountUrl: ['', []],
    accountName: ['', []],
    endpointSuffix: ['core.windows.net', []],
    tenantId: ['', []],
    clientId: ['', []],
    managedIdentityClientId: ['', []],
    pathPrefix: ['', []],
    createContainerIfNotExists: [true, []],
    // How many records share one blob. Bulk: every run writes one new timestamped NDJSON blob for the whole
    // batch (object storage's natural "write-once" shape, e.g. for a data-lake consumer) — no per-record
    // identity. Individual: one blob per record — recordMode below then decides what happens to each one.
    granularity: ['bulk', []],
    // Only meaningful when granularity is 'individual'. Insert: always add a fresh blob, never overwriting one
    // that already exists under the same key. Upsert: create-or-overwrite the key-derived blob unconditionally
    // (matches SQL/Mongo's own upsert mode). Update: only overwrite a blob that already exists; skip the record
    // entirely if it doesn't.
    recordMode: ['upsert', []],
    // Only meaningful when granularity is 'individual'. Blank means "use the record mode's own default" (see
    // BlobDestinationSettings.Parse's ParseFolderPattern/ParseFileNamePattern) — left blank rather than
    // pre-filled with a mode-specific literal so switching recordMode later doesn't leave a stale pattern behind.
    folderPattern: ['', [Validators.maxLength(512), Validators.pattern(BlobStorageDestinationFormComponent.BLOB_FOLDER_NAMING_PATTERN)]],
    fileNamePattern: ['', [Validators.maxLength(512), Validators.pattern(BlobStorageDestinationFormComponent.BLOB_FILE_NAME_PATTERN)]],
  });

  /** True while the host is reusing a previously-saved connection unchanged — secretValue is a secret that is
   *  never repopulated when patching from an existing connection, so requiring it here would permanently block
   *  reuse unless the user retypes it just to satisfy validation. */
  readonly reusingExisting = input<boolean>(false);
  /** Set alongside reusingExisting — when present, testConnection() sends it instead of a blank secret so the
   *  backend can resolve the stored secret server-side (see BlobDestinationConnectionTestService). */
  readonly existingDestinationId = input<string | null>(null);

  constructor() {
    effect(() => {
      const authMode = this.blobForm.controls.authMode.value;
      const reusing = this.reusingExisting();
      untracked(() => this._syncAuthModeValidators(authMode, reusing));
    });
    this.blobForm.controls.authMode.valueChanges.subscribe(v =>
      this._syncAuthModeValidators(v, this.reusingExisting()));
  }

  /** Which fields are required depends on the selected auth mode — mirrors the backend's
   *  BlobDestinationSettings.Parse validation (accountKey needs accountName; managedIdentity/servicePrincipal
   *  need accountUrl; servicePrincipal also needs tenantId/clientId). secretValue is required for every mode
   *  except managedIdentity (which never resolves a Key Vault secret) — and only for a genuinely new
   *  connection, since reusingExisting never repopulates it. */
  private _syncAuthModeValidators(authMode: string | null, reusingExisting: boolean): void {
    const requiresSecret = authMode !== 'managedIdentity' && !reusingExisting;
    const secretCtrl = this.blobForm.get('secretValue')!;
    secretCtrl.setValidators(requiresSecret ? [Validators.required] : []);
    secretCtrl.updateValueAndValidity({ emitEvent: false });

    const accountNameCtrl = this.blobForm.get('accountName')!;
    accountNameCtrl.setValidators(authMode === 'accountKey' ? [Validators.required] : []);
    accountNameCtrl.updateValueAndValidity({ emitEvent: false });

    const requiresAccountUrl = authMode === 'managedIdentity' || authMode === 'servicePrincipal';
    const accountUrlCtrl = this.blobForm.get('accountUrl')!;
    accountUrlCtrl.setValidators(requiresAccountUrl ? [Validators.required] : []);
    accountUrlCtrl.updateValueAndValidity({ emitEvent: false });

    const requiresServicePrincipal = authMode === 'servicePrincipal';
    const tenantCtrl = this.blobForm.get('tenantId')!;
    tenantCtrl.setValidators(requiresServicePrincipal ? [Validators.required] : []);
    tenantCtrl.updateValueAndValidity({ emitEvent: false });
    const clientCtrl = this.blobForm.get('clientId')!;
    clientCtrl.setValidators(requiresServicePrincipal ? [Validators.required] : []);
    clientCtrl.updateValueAndValidity({ emitEvent: false });
  }

  isValid(): boolean {
    return this.blobForm.valid;
  }

  /** The probe needs a container plus a secret for every auth mode except Managed Identity (which resolves no
   *  secret) — UNLESS reusing an existing connection unchanged, in which case a blank secretValue is fine: the
   *  backend resolves the stored secret server-side instead (see BlobDestinationConnectionTestService, and
   *  existingDestinationId below). Mode-specific extras (account name, tenant/client id) are validated
   *  server-side with clear errors. */
  canTest(): boolean {
    const v = this.blobForm.value;
    if (!v.container) return false;
    if (v.authMode === 'managedIdentity') return true;
    return !!v.secretValue || (this.reusingExisting() && !!this.existingDestinationId());
  }

  /** Live connectivity check before saving: builds the container client for the chosen auth mode and does a
   *  reachability round-trip server-side (see BlobDestinationConnectionTestService). Never blocks Save. */
  testConnection(): void {
    if (!this.canTest()) return;
    const v = this.blobForm.value;
    const id = this.existingDestinationId();
    console.log(
      `[Blob Test Connection] authMode=${v.authMode} reusingExisting=${this.reusingExisting()} ` +
      `existingDestinationId=${id ?? '(none)'} secretTyped=${!!v.secretValue} ` +
      `=> ${!v.secretValue && id ? 'resolving stored secret server-side' : 'using the form\'s own (typed) secret'}`,
    );
    this.probeState.set('testing');
    this.probeError.set(null);
    this.schemaSvc.testBlob({
      authMode: v.authMode ?? 'connectionString',
      container: v.container ?? '',
      secret: v.secretValue ?? '',
      accountUrl: v.accountUrl ?? '',
      accountName: v.accountName ?? '',
      endpointSuffix: v.endpointSuffix ?? 'core.windows.net',
      tenantId: v.tenantId ?? '',
      clientId: v.clientId ?? '',
      managedIdentityClientId: v.managedIdentityClientId ?? '',
      destinationId: id ?? undefined,
    }).subscribe({
      next: res => {
        console.log(`[Blob Test Connection] result connected=${res.connected}${res.connected ? '' : ` — ${res.error ?? 'no error message'}`}`);
        if (res.connected) {
          this.probeState.set('ok');
        } else {
          this.probeState.set('error');
          this.probeError.set(res.error ?? 'Connection failed.');
        }
      },
      error: err => {
        console.error(`[Blob Test Connection] FAILED for destinationId=${id ?? '(none)'}`, err);
        this.probeState.set('error');
        this.probeError.set(err?.error?.error ?? err?.error?.detail ?? err?.message ?? 'Connection failed.');
      },
    });
  }

  getRawValue(): Record<string, unknown> {
    return this.blobForm.getRawValue();
  }

  getFullConfig(): Record<string, string> {
    const v = this.blobForm.value;
    return {
      dest_name: v.name ?? '',
      dest_blobContainer: v.container ?? '',
      dest_blobAuthMode: v.authMode ?? 'connectionString',
      dest_blobSecret: v.secretValue ?? '',
      dest_blobAccountUrl: v.accountUrl ?? '',
      dest_blobAccountName: v.accountName ?? '',
      dest_blobEndpointSuffix: v.endpointSuffix ?? 'core.windows.net',
      dest_blobTenantId: v.tenantId ?? '',
      dest_blobClientId: v.clientId ?? '',
      dest_blobManagedIdentityClientId: v.managedIdentityClientId ?? '',
      dest_blobPathPrefix: v.pathPrefix ?? '',
      dest_blobCreateContainerIfNotExists: String(v.createContainerIfNotExists ?? true),
      dest_blobGranularity: v.granularity ?? 'bulk',
      dest_blobRecordMode: v.recordMode ?? 'upsert',
      dest_blobFolderPattern: v.folderPattern ?? '',
      dest_blobFileNamePattern: v.fileNamePattern ?? '',
    };
  }

  getMetadata(): { fields: Record<string, string>; secret?: string | null } | null {
    if (!this.isValid()) return null;
    const config = this.getFullConfig();
    return {
      fields: JSON.parse(buildConnectionMetadata(config, 'blob')) as Record<string, string>,
      // Managed Identity never resolves a Key Vault secret (see BlobDestinationSettings.RequiresSecret
      // server-side) — no secret to send for that mode.
      secret: config['dest_blobAuthMode'] === 'managedIdentity' ? '' : (config['dest_blobSecret'] || ''),
    };
  }

  patchFrom(fields: Record<string, string>, target?: string | null): void {
    this.blobForm.patchValue({
      name: fields['dest_name'] || this.blobForm.value.name || 'Azure Blob Export',
      container: fields['dest_blobContainer'] || target || '',
      authMode: fields['dest_blobAuthMode'] || 'connectionString',
      secretValue: '',
      accountUrl: fields['dest_blobAccountUrl'] || '',
      accountName: fields['dest_blobAccountName'] || '',
      endpointSuffix: fields['dest_blobEndpointSuffix'] || 'core.windows.net',
      tenantId: fields['dest_blobTenantId'] || '',
      clientId: fields['dest_blobClientId'] || '',
      managedIdentityClientId: fields['dest_blobManagedIdentityClientId'] || '',
      pathPrefix: fields['dest_blobPathPrefix'] || '',
      createContainerIfNotExists: fields['dest_blobCreateContainerIfNotExists'] !== 'false',
      granularity: fields['dest_blobGranularity'] || 'bulk',
      recordMode: fields['dest_blobRecordMode'] || 'upsert',
      folderPattern: fields['dest_blobFolderPattern'] || '',
      fileNamePattern: fields['dest_blobFileNamePattern'] || '',
    });
    this._syncAuthModeValidators(this.blobForm.value.authMode ?? null, this.reusingExisting());
  }

  reset(): void {
    this.blobForm.reset({
      name: 'Azure Blob Export', container: '', authMode: 'connectionString', secretValue: '',
      accountUrl: '', accountName: '', endpointSuffix: 'core.windows.net', tenantId: '', clientId: '',
      managedIdentityClientId: '', pathPrefix: '', createContainerIfNotExists: true,
      granularity: 'bulk', recordMode: 'upsert', folderPattern: '', fileNamePattern: '',
    });
    this._syncAuthModeValidators(this.blobForm.value.authMode ?? null, this.reusingExisting());
  }
}
