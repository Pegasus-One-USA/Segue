import { Component, input, output, signal } from '@angular/core';
import { LaunchStandaloneProviderComponent } from '../provider-standalone/launch-standalone-provider';
import { readProviderStandaloneSessionId } from '../provider-standalone/core/services/provider-standalone-session-id';
import { Resource11BrowserComponent } from './resource-11-browser';
import { isNew11Enabled } from './new11-flag';

// Explicit, curated Epic sandbox practitioner ids for Provider Standalone's New 11 → Practitioners import — sent
// as-is on every Import Practitioners click, overriding the "missing ids" auto-discovery every other role still
// uses (see Resource11Endpoints' PractitionerIds handling and Resource11BrowserComponent.practitionerIds).
const PROVIDER_STANDALONE_PRACTITIONER_IDS = [
  'eM5CWtq15N0WJeuCet5bJlQ3','e9s-IdXQOUVywHOVoisd6xQ3','eJNlquV-tJUJuScDWkfQTmA3','eU3aNE-vCGLgH6ukixYqTFA3','erqn5oyiA9.LpVQkQ2Ulmgg3'
];

// ProviderStandalone role shell: "Default | New 11" menu. Default keeps the existing standalone-provider launch
// screen untouched; New 11 shows the curated _11 tables.
@Component({
  selector: 'app-provider-standalone-new11',
  standalone: true,
  imports: [LaunchStandaloneProviderComponent, Resource11BrowserComponent],
  templateUrl: './provider-standalone-new11.html',
})
export class ProviderStandaloneNew11Component {
  readonly loginTypeLabel = input('');
  readonly logout = output<void>();
  readonly view = signal<'default' | 'new11'>('default');
  // See PatientNew11Component's new11Enabled remarks — same read-once-at-construction pattern.
  readonly new11Enabled = isNew11Enabled();

  // Read fresh on every switch to the New 11 tab (not cached at construction) — the Default tab's own sign-in
  // flow is what actually persists this, and a user can switch to New 11 for the first time only after signing in
  // there. See Resource11BrowserComponent.callerId's remarks.
  providerCallerId(): string | null {
    return readProviderStandaloneSessionId();
  }

  readonly practitionerIds = PROVIDER_STANDALONE_PRACTITIONER_IDS;
}
