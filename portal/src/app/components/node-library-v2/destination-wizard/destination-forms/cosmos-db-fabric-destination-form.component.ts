import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { buildConnectionMetadata } from '../../../../destination-connections/utils/destination-connection-secret.util';
import { DestinationSchemaService } from '../../../../services/destination-schema.service';
import { WizardDestinationFormApi } from './destination-form-api';

/**
 * Cosmos DB in Microsoft Fabric — documents into a container over the Cosmos NoSQL data plane.
 *
 * Three things this form deliberately does not offer, each because the service does not support it:
 * - **Auth beyond Entra.** Cosmos DB in Fabric relies exclusively on Microsoft Entra ID and built-in data-plane
 *   roles. There are no account keys or connection strings to paste, so this has the same two auth modes as the
 *   other Fabric surfaces rather than the wider set a self-hosted database would need.
 * - **Creating a container on a key nobody chose.** A container's partition key is fixed at creation and can
 *   never be changed, and the cost of a wrong one is rebuilding the container and re-loading its data. Creation
 *   is therefore offered as an explicit three-way choice (never / use my key / use the default), not as a
 *   silent convenience — see the containerCreationMode control.
 * - **A connection mode.** Fabric supports only Gateway connectivity, so there is nothing to choose — the
 *   backend pins it (the .NET SDK defaults to Direct, which cannot connect at all).
 */
@Component({
  selector: 'app-cosmos-db-fabric-destination-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './cosmos-db-fabric-destination-form.component.html',
  styleUrls: ['../destination-wizard.component.scss'],
})
export class CosmosDbFabricDestinationFormComponent implements WizardDestinationFormApi {
  /** Discriminator for isCosmosDbFabricForm — see that guard's doc comment for why this is a marker rather
   *  than a duck-type on testConnection/probeState. */
  readonly kind = 'cosmosFabric' as const;

  private readonly fb = inject(FormBuilder);
  private readonly schemaSvc = inject(DestinationSchemaService);

  /** True while the host is reusing a previously-saved connection unchanged — the client secret is never
   *  repopulated when patching from an existing connection, so requiring it would block reuse. */
  readonly reusingExisting = input<boolean>(false);

  /** The saved destination's id when reusing one, so Test Connection can resolve its stored client secret
   *  instead of demanding it be retyped (the form never re-displays a stored secret). */
  readonly existingDestinationId = input<string | null>(null);

  /** Whether the Advanced disclosure is expanded. Collapsed by default: every control inside it has a working
   *  default, so a first-time connection never needs to open it. */
  readonly advancedOpen = signal(false);

  toggleAdvanced(): void {
    this.advancedOpen.update(open => !open);
  }

  readonly cosmosForm = this.fb.group({
    name: ['Cosmos DB in Fabric', [Validators.required]],
    /** From the database's Settings > Connection section in Fabric. Not derivable from anything else on the
     *  destination, which is why it is required rather than defaulted. */
    endpoint: ['', [Validators.required, Validators.pattern(/^https?:\/\/.+/i)]],
    database: ['', [Validators.required]],
    /** Blank means "use the mapping's own destination object", so one destination can serve many resource
     *  types — the same defaulting every other per-object destination does. */
    container: ['', []],
    authMode: ['managedIdentity', [Validators.required]],
    secretValue: ['', []],
    tenantId: ['', []],
    clientId: ['', []],
    managedIdentityClientId: ['', []],
    authorityHost: ['', []],
    /** The partition key a container is CREATED with under 'useConfiguredPartitionKey'. Under 'never' it is
     *  documentation only — a record of which key the existing container was built with. */
    partitionKeyPath: ['', []],
    /** What happens when a mapping's target container does not exist. Defaults to 'never', the only mode
     *  under which FHIRBridge cannot commit the customer to an irreversible partition key. */
    containerCreationMode: ['never', [Validators.required]],
  });

  readonly isServicePrincipal = computed(() => this.authModeValue() === 'servicePrincipal');

  private readonly authModeValue = signal<string>('managedIdentity');

  private readonly creationModeValue = signal<string>('never');

  /** The partition key used when nothing is configured — mirrors
   *  CosmosDbFabricDestinationSettings.DefaultPartitionKeyPath, and shown in the warning so the user sees the
   *  key they are accepting rather than just the words "the default". */
  readonly defaultPartitionKeyPath = '/id';

  /** True for the mode that creates containers on a key the user did not choose. Drives the warning banner:
   *  the choice is irreversible, so it is stated at the point of selection rather than left in a tooltip. */
  readonly warnsAboutDefaultPartitionKey = computed(
    () => this.creationModeValue() === 'useDefaultPartitionKey'
      && !this.cosmosForm.controls.partitionKeyPath.value?.trim());

  /** Real container names from the last successful Test Connection — seeds the mapping canvas's picker, the
   *  same role MongoDestinationFormComponent.collections plays. */
  readonly containers = signal<string[]>([]);

  readonly probeState = signal<'idle' | 'testing' | 'ok' | 'error'>('idle');
  readonly probeError = signal<string | null>(null);

  constructor() {
    effect(() => {
      const authMode = this.cosmosForm.controls.authMode.value;
      const reusing = this.reusingExisting();
      untracked(() => this._syncAuthModeValidators(authMode, reusing));
    });

    this.cosmosForm.controls.authMode.valueChanges.subscribe(mode => {
      this.authModeValue.set(mode ?? 'managedIdentity');
      this._syncAuthModeValidators(mode, this.reusingExisting());
    });

    this.cosmosForm.controls.containerCreationMode.valueChanges.subscribe(mode => {
      this.creationModeValue.set(mode ?? 'never');
      this._syncCreationModeValidators(mode);
    });

    // A key typed after the mode was chosen flips the warning off, so the banner tracks the real state
    // rather than only the moment the radio was clicked.
    this.cosmosForm.controls.partitionKeyPath.valueChanges.subscribe(
      () => this.creationModeValue.set(this.cosmosForm.controls.containerCreationMode.value ?? 'never'));
  }

  /** Mirrors CosmosDbFabricDestinationSettings.ParseContainerCreationMode: "create using my configured key"
   *  requires a key. The backend REFUSES that combination rather than falling back to the default, so the
   *  form must too — silently downgrading here would hand the user the very outcome the mode rejects. */
  private _syncCreationModeValidators(mode: string | null): void {
    const control = this.cosmosForm.controls.partitionKeyPath;
    control.setValidators(mode === 'useConfiguredPartitionKey' ? [Validators.required] : []);
    control.updateValueAndValidity({ emitEvent: false });
  }

  /** Mirrors CosmosDbFabricDestinationSettings.Parse: a service principal needs tenant id, client id and a
   *  secret; managed identity needs none of the three. */
  private _syncAuthModeValidators(authMode: string | null, reusingExisting: boolean): void {
    const isServicePrincipal = authMode === 'servicePrincipal';

    const secret = this.cosmosForm.get('secretValue')!;
    secret.setValidators(isServicePrincipal && !reusingExisting ? [Validators.required] : []);
    secret.updateValueAndValidity({ emitEvent: false });

    for (const name of ['tenantId', 'clientId']) {
      const control = this.cosmosForm.get(name)!;
      control.setValidators(isServicePrincipal ? [Validators.required] : []);
      control.updateValueAndValidity({ emitEvent: false });
    }
  }

  showError(name: string): boolean {
    const control = this.cosmosForm.get(name);
    return !!control && control.invalid && control.touched;
  }

  /** True when any control inside the Advanced disclosure is invalid — the host expands it rather than leaving
   *  the user staring at a disabled Next with no visible cause. */
  hasAdvancedError(): boolean {
    return ['authorityHost', 'managedIdentityClientId', 'partitionKeyPath']
      .some(name => this.showError(name));
  }

  isValid(): boolean {
    return this.cosmosForm.valid;
  }

  getRawValue(): Record<string, unknown> {
    return this.cosmosForm.getRawValue();
  }

  getFullConfig(): Record<string, string> {
    const v = this.cosmosForm.value;
    return {
      dest_name: v.name ?? '',
      dest_cosmosFabricEndpoint: v.endpoint ?? '',
      dest_cosmosFabricDatabase: v.database ?? '',
      dest_cosmosFabricContainer: v.container ?? '',
      dest_cosmosFabricAuthMode: v.authMode ?? 'managedIdentity',
      dest_cosmosFabricSecret: v.secretValue ?? '',
      dest_cosmosFabricTenantId: v.tenantId ?? '',
      dest_cosmosFabricClientId: v.clientId ?? '',
      dest_cosmosFabricManagedIdentityClientId: v.managedIdentityClientId ?? '',
      dest_cosmosFabricAuthorityHost: v.authorityHost ?? '',
      dest_cosmosFabricPartitionKeyPath: v.partitionKeyPath ?? '',
      dest_cosmosFabricContainerCreationMode: v.containerCreationMode ?? 'never',
    };
  }

  /** Endpoint + database are the minimum the probe needs; a saved destination can test on its stored secret. */
  canTest(): boolean {
    const v = this.cosmosForm.value;
    return !!v.endpoint?.trim() && !!v.database?.trim();
  }

  /**
   * Live connectivity check before saving: reads the database and lists its containers server-side (see
   * CosmosDbFabricDestinationConnectionTestService).
   *
   * The container list is the substantive half. Under the default 'never' creation mode the target container
   * must already exist, so a typo previously surfaced only as a failed pipeline run — this is what moves that
   * to the wizard. Never blocks Save; `onSettled` lets the wizard's Next advance on success, mirroring
   * MongoDestinationFormComponent.testConnection.
   */
  testConnection(onSettled?: (result: { connected: boolean }) => void): void {
    if (!this.canTest()) return;
    this.probeState.set('testing');
    this.probeError.set(null);

    const v = this.cosmosForm.value;
    this.schemaSvc
      .testCosmosDbFabric({
        endpoint: v.endpoint ?? '',
        database: v.database ?? '',
        authMode: v.authMode ?? 'managedIdentity',
        tenantId: v.tenantId ?? undefined,
        clientId: v.clientId ?? undefined,
        // Blank on a reused connection — the service then resolves the stored secret from the vault.
        secret: v.secretValue ?? undefined,
        managedIdentityClientId: v.managedIdentityClientId ?? undefined,
        authorityHost: v.authorityHost ?? undefined,
        destinationId: this.existingDestinationId() ?? undefined,
      })
      .subscribe({
        next: res => {
          this.containers.set(res.containers ?? []);
          if (res.connected) {
            this.probeState.set('ok');
          } else {
            this.probeState.set('error');
            this.probeError.set(res.error ?? 'Connection failed.');
          }
          onSettled?.({ connected: res.connected });
        },
        error: err => {
          this.probeState.set('error');
          this.probeError.set(err?.error?.error ?? err?.error?.detail ?? err?.message ?? 'Connection failed.');
          onSettled?.({ connected: false });
        },
      });
  }

  getMetadata(): { fields: Record<string, string>; secret?: string | null } | null {
    if (!this.isValid()) return null;
    const config = this.getFullConfig();
    return {
      fields: JSON.parse(buildConnectionMetadata(config, 'cosmosFabric')) as Record<string, string>,
      // Managed identity never resolves a Key Vault secret (see
      // CosmosDbFabricDestinationSettings.RequiresSecret).
      secret: config['dest_cosmosFabricAuthMode'] === 'servicePrincipal'
        ? (config['dest_cosmosFabricSecret'] || '')
        : '',
    };
  }

  patchFrom(fields: Record<string, string>, target?: string | null): void {
    this.cosmosForm.patchValue({
      name: fields['dest_name'] || this.cosmosForm.value.name || 'Cosmos DB in Fabric',
      endpoint: fields['dest_cosmosFabricEndpoint'] || '',
      database: fields['dest_cosmosFabricDatabase'] || '',
      // Target is the DestinationConfigurationDto's own column, which for this type carries the container —
      // the same dual read every other per-object destination form does.
      container: fields['dest_cosmosFabricContainer'] || target || '',
      authMode: fields['dest_cosmosFabricAuthMode'] || 'managedIdentity',
      secretValue: '',
      tenantId: fields['dest_cosmosFabricTenantId'] || '',
      clientId: fields['dest_cosmosFabricClientId'] || '',
      managedIdentityClientId: fields['dest_cosmosFabricManagedIdentityClientId'] || '',
      authorityHost: fields['dest_cosmosFabricAuthorityHost'] || '',
      partitionKeyPath: fields['dest_cosmosFabricPartitionKeyPath'] || '',
    });
    this.authModeValue.set(this.cosmosForm.value.authMode ?? 'managedIdentity');
    this._syncAuthModeValidators(this.cosmosForm.value.authMode ?? null, this.reusingExisting());
  }

  reset(): void {
    this.cosmosForm.reset({
      name: 'Cosmos DB in Fabric',
      endpoint: '',
      database: '',
      container: '',
      authMode: 'managedIdentity',
      secretValue: '',
      tenantId: '',
      clientId: '',
      managedIdentityClientId: '',
      authorityHost: '',
      partitionKeyPath: '',
    });
    this.authModeValue.set('managedIdentity');
    this._syncAuthModeValidators('managedIdentity', false);
    this.advancedOpen.set(false);
  }
}
