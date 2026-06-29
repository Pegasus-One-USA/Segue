import { Component, input, inject, signal, computed } from '@angular/core';
import { ReactiveFormsModule, FormBuilder } from '@angular/forms';
import { ToastService } from '../../../../services/toast.service';
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

  readonly keyGenStatus = signal<'idle' | 'generated'>('idle');

  readonly form = this.fb.nonNullable.group({
    clientAuth:       ['public'],
    secretRef:        [''],
    secretStore:      ['azure-kv'],
    keySource:        ['gen'],
    signingAlgorithm: ['RS384'],
    jwksUrl:          [''],
    keyId:            [''],
    keyVaultRef:      [''],
    stateNonce:       ['enabled'],
    refreshToken:     ['online_access'],
    tokenStorage:     ['memory'],
    clockSkew:        ['60'],
  });

  get isPublic(): boolean  { return this.form.controls.clientAuth.value === 'public'; }
  get isSecret(): boolean  { return this.form.controls.clientAuth.value === 'secret'; }
  get isJwt(): boolean     { return this.form.controls.clientAuth.value === 'jwt'; }
  get isGenKey(): boolean  { return this.form.controls.keySource.value === 'gen'; }

  generateKeyPair(): void {
    const kid = 'fb-' + Math.random().toString(36).slice(2, 10).toUpperCase();
    this.form.controls.jwksUrl.setValue('https://fhirbridge.com/.well-known/jwks.json');
    this.form.controls.keyId.setValue(kid);
    this.keyGenStatus.set('generated');
    this.toast.show('Key pair generated', 'JWKS URL and Key ID populated. Register this URL in Epic.');
  }

  validate(): boolean {
    if (this.isJwt && this.isGenKey && this.keyGenStatus() !== 'generated') {
      this.toast.show('Generate key pair', 'Generate a key pair before proceeding, or choose a different key source.');
      return false;
    }
    if (this.isJwt && !this.isGenKey && !this.form.controls.jwksUrl.value.trim()) {
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
      jwksMethod:       v.keySource === 'gen' ? 'hosted' : 'external',
    };
  }
}
