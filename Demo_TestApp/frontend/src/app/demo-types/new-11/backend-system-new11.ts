import { Component, input, output, signal } from '@angular/core';
import { BackendSystemComponent } from '../backend-system/backend-system';
import { Resource11BrowserComponent } from './resource-11-browser';
import { isNew11Enabled } from './new11-flag';

// BackendSystem role shell: "Default | New 11" menu. Default keeps the existing patient-centric BackendSystem
// browser (the *_NewMapped / Encounter / ... tables) untouched; New 11 shows the curated _11 tables.
@Component({
  selector: 'app-backend-system-new11',
  standalone: true,
  imports: [BackendSystemComponent, Resource11BrowserComponent],
  templateUrl: './backend-system-new11.html',
})
export class BackendSystemNew11Component {
  readonly loginTypeLabel = input('');
  readonly logout = output<void>();
  readonly view = signal<'default' | 'new11'>('default');
  // See PatientNew11Component's new11Enabled remarks — same read-once-at-construction pattern.
  readonly new11Enabled = isNew11Enabled();
}
