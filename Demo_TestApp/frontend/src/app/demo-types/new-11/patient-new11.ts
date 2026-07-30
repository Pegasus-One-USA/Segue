import { Component, input, output, signal } from '@angular/core';
import { PatientStandaloneComponent } from '../demo-type-1/patient-standalone';
import { Resource11BrowserComponent } from './resource-11-browser';
import { isNew11Enabled } from './new11-flag';

// Patient role shell: adds the two-item "Default | New 11" menu. Default keeps the existing patient dashboard
// untouched; New 11 shows the curated _11 tables. Same loginTypeLabel input / logout output as the component it
// replaces in app.html, so the shell swaps in transparently.
@Component({
  selector: 'app-patient-new11',
  standalone: true,
  imports: [PatientStandaloneComponent, Resource11BrowserComponent],
  templateUrl: './patient-new11.html',
})
export class PatientNew11Component {
  readonly loginTypeLabel = input('');
  readonly logout = output<void>();
  readonly view = signal<'default' | 'new11'>('default');
  // Read once at construction, same as app.ts's isRealEhrLaunch/isProviderInAppLaunch — this app has no router, so
  // a live component instance can't have the flag change out from under it. Set via Admin Settings' Default tab
  // (AdminSettingsComponent.toggleNew11Enabled), backed by a cookie — see new11-flag.ts.
  readonly new11Enabled = isNew11Enabled();
}
