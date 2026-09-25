import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { buildConnectionMetadata } from '../../../../destination-connections/utils/destination-connection-secret.util';
import { WizardDestinationFormApi } from './destination-form-api';

/**
 * Cosmos DB in Microsoft Fabric — documents into a container over the Cosmos NoSQL data plane.
 *
 * Three things this form deliberately does not offer, each because the service does not support it:
 * - **Auth beyond Entra.** Cosmos DB in Fabric relies exclusively on Microsoft Entra ID and built-in data-plane
 *   roles. There are no account keys or connection strings to paste, so this has the same two auth modes as the
 *   other Fabric surfaces rather than the wider set a self-hosted database would need.
 * - **Creating the container.** A container's partition key is fixed at creation and can never be changed, so
 *   choosing one here would silently commit the customer to a layout they did not pick, and the cost of the
 *   wrong choice is rebuilding the container and re-loading its data. The container must already exist.
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
  private readonly fb = inject(FormBuilder);

  /** True while the host is reusing a previously-saved connection unchanged — the client secret is never
   *  repopulated when patching from an existing connection, so requiring it would block reuse. */
  readonly reusingExisting = input<boolean>(false);

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
    /** Recorded for documentation only — the backend never creates a container, so this is not used to make
     *  one. It is here because a reader of the saved configuration otherwise has no record of which key the
     *  target container was built with. */
    partitionKeyPath: ['', []],
  });

  readonly isServicePrincipal = computed(() => this.authModeValue() === 'servicePrincipal');

  private readonly authModeValue = signal<string>('managedIdentity');

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
    };
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
