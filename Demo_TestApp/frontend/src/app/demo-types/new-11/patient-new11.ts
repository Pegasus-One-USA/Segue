import { Component, input, output, signal } from '@angular/core';
import { PatientStandaloneComponent } from '../demo-type-1/patient-standalone';
import { Resource11BrowserComponent } from './resource-11-browser';

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
}
