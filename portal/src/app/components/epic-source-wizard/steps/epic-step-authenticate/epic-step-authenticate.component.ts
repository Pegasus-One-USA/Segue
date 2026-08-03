import { Component, input, inject, signal } from '@angular/core';
import { ReactiveFormsModule, FormBuilder } from '@angular/forms';
import { ToastService } from '../../../../services/toast.service';
import { ISourceConnectionService } from '../../../../source-connections/services/i-source-connection.service';
import { AuthValues } from '../../models/epic-config.model';

@Component({
  selector: 'app-epic-step-authenticate',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './epic-step-authenticate.component.html',
  styleUrl: './epic-step-authenticate.component.scss',
})
export class EpicStepAuthenticateComponent {
  readonly showAdvanced = input(false);

  private readonly toast = inject(ToastService);
  private readonly fb    = inject(FormBuilder);
  private readonly sourceConnectionSvc = inject(ISourceConnectionService);

  readonly keyGenStatus = signal<'idle' | 'generating' | 'generated'>('idle');
  /** Name of the file chosen for "Import Existing Private Key" — read client-side, never uploaded until Import
   *  is clicked, so choosing the wrong file and re-choosing before importing costs nothing server-side. */
  readonly selectedFileName = signal<string | null>(null);
  private pendingPrivateKeyPem: string | null = null;

  readonly form = this.fb.nonNullable.group({
    clientAuth:       ['public'],
    secretRef:        [''],
    secretStore:      ['azure-kv'],
    keySource:        ['gen'],
    signingAlgorithm: ['RS384'],
    jwksUrl:          [''],
    keyId:            [''],
    keyVaultRef:      [''],
    secretName:       [''],
    stateNonce:       ['enabled'],
    refreshToken:     ['online_access'],
    tokenStorage:     ['memory'],
    clockSkew:        ['60'],
  });

  get isPublic(): boolean  { return this.form.controls.clientAuth.value === 'public'; }
  get isSecret(): boolean  { return this.form.controls.clientAuth.value === 'secret'; }
  get isJwt(): boolean     { return this.form.controls.clientAuth.value === 'jwt'; }
  get isGenKey(): boolean    { return this.form.controls.keySource.value === 'gen'; }
  get isImportKey(): boolean { return this.form.controls.keySource.value === 'import'; }
  /** Both "gen" and "import" are server-managed: FHIRBridge holds the private key and serves its own JWKS for
   *  it, so neither needs (or shows) the manual JWKS URL/Key Vault Reference fields the truly-external options do. */
  get isServerManagedKey(): boolean { return this.isGenKey || this.isImportKey; }

  /** Generates a real RSA key pair server-side (SigningKeyGenerationService) and stores the private key in the
   *  secret store — no key material ever reaches this component. The JWKS URL is only known once this connection
   *  is saved and has an id (SourceJwksController is routed per-connection), so this shows the Key ID and the
   *  Key Vault/secret reference now and leaves the exact URL for after save. */
  generateKeyPair(): void {
    this.keyGenStatus.set('generating');
    this.sourceConnectionSvc.generateSigningKey().subscribe({
      next: (key) => {
        this.form.controls.keyId.setValue(key.keyId);
        this.form.controls.keyVaultRef.setValue(key.keyVaultName);
        this.form.controls.secretName.setValue(key.secretName);
        this.form.controls.jwksUrl.setValue('');
        this.keyGenStatus.set('generated');
        this.toast.show(
          'Key pair generated',
          `Key ID ${key.keyId} generated. The JWKS URL becomes available at this connection's ` +
          '.well-known/jwks.json once you save — register that URL in Epic.'
        );
      },
      error: (err) => {
        this.keyGenStatus.set('idle');
        const message = err?.error?.title ?? err?.message ?? 'Failed to generate a key pair.';
        this.toast.show('Key generation failed', message, 'error');
      },
    });
  }

  /** Reads the chosen file client-side only — nothing is sent to the backend until "Import Private Key" is
   *  clicked, so picking the wrong file and re-picking costs nothing. */
  onPrivateKeyFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;

    this.pendingPrivateKeyPem = null;
    this.keyGenStatus.set('idle');
    this.selectedFileName.set(file.name);

    const reader = new FileReader();
    reader.onload = () => {
      this.pendingPrivateKeyPem = reader.result as string;
    };
    reader.onerror = () => {
      this.selectedFileName.set(null);
      this.toast.show('Could not read file', 'Choose the file again.', 'error');
    };
    reader.readAsText(file);
  }

  /** Sends the selected file's contents to the backend, which validates it's a real, unencrypted RSA private key
   *  (rejecting a public key, certificate, non-RSA key, or a weak/encrypted one) before storing it — the same
   *  secret-store path generateKeyPair() uses, so the connection's Authentication config is wired up identically
   *  either way. */
  importPrivateKey(): void {
    if (!this.pendingPrivateKeyPem) {
      this.toast.show('Choose a file', 'Choose your private key file before importing.');
      return;
    }

    this.keyGenStatus.set('generating');
    this.sourceConnectionSvc.importSigningKey(this.pendingPrivateKeyPem).subscribe({
      next: (key) => {
        this.form.controls.keyId.setValue(key.keyId);
        this.form.controls.keyVaultRef.setValue(key.keyVaultName);
        this.form.controls.secretName.setValue(key.secretName);
        this.form.controls.jwksUrl.setValue('');
        this.keyGenStatus.set('generated');
        this.toast.show(
          'Private key imported',
          `Key ID ${key.keyId} imported. The JWKS URL becomes available at this connection's ` +
          '.well-known/jwks.json once you save — register that URL in Epic.'
        );
      },
      error: (err) => {
        this.keyGenStatus.set('idle');
        const message = err?.error?.message ?? err?.error?.error ?? 'Failed to import the private key.';
        this.toast.show('Import failed', message, 'error');
      },
    });
  }

  validate(): boolean {
    if (this.isJwt && this.isServerManagedKey && this.keyGenStatus() !== 'generated') {
      const action = this.isImportKey ? 'Import your private key' : 'Generate a key pair';
      this.toast.show(`${action} first`, `${action} before proceeding, or choose a different key source.`);
      return false;
    }
    if (this.isJwt && !this.isServerManagedKey && !this.form.controls.jwksUrl.value.trim()) {
      this.toast.show('JWKS URL required', 'Enter the JWKS URL for your key.');
      return false;
    }
    return true;
  }

  getAuthValues(): AuthValues {
    const v = this.form.value;
    return {
      clientAuth:       v.clientAuth ?? 'public',
      secretRef:        v.secretRef ?? '',
      secretStore:      v.secretStore ?? 'azure-kv',
      keySource:        v.keySource ?? 'gen',
      signingAlgorithm: v.signingAlgorithm ?? 'RS384',
      jwksUrl:          v.jwksUrl ?? '',
      keyId:            v.keyId ?? '',
      keyVaultRef:      v.keyVaultRef ?? '',
      secretName:       v.secretName ?? '',
      jwksMethod:       (v.keySource === 'gen' || v.keySource === 'import') ? 'hosted' : 'external',
    };
  }
}
