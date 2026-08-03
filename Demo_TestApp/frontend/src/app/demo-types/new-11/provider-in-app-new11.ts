import { Component, input, output, signal } from '@angular/core';
import { LaunchProviderInAppComponent } from '../demo-type-2/launch-provider-in-app';
import { Resource11BrowserComponent } from './resource-11-browser';
import { isNew11Enabled } from './new11-flag';

// ProviderInApp role shell: "Default | New 11" menu. Only used for a plain ProviderInApp login (no live EHR launch
// on the URL) — a real Epic launch with iss/launch is handled directly in app.html and never reaches this shell,
// so the Default tab here always renders LaunchProviderInAppComponent in its mock-data mode.
@Component({
  selector: 'app-provider-in-app-new11',
  standalone: true,
  imports: [LaunchProviderInAppComponent, Resource11BrowserComponent],
  templateUrl: './provider-in-app-new11.html',
})
export class ProviderInAppNew11Component {
  readonly loginTypeLabel = input('');
  readonly logout = output<void>();
  readonly view = signal<'default' | 'new11'>('default');
  // See PatientNew11Component's new11Enabled remarks — same read-once-at-construction pattern.
  readonly new11Enabled = isNew11Enabled();
}
