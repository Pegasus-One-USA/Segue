import { Component, input, output, signal } from '@angular/core';
import { LaunchStandaloneProviderComponent } from '../provider-standalone/launch-standalone-provider';
import { Resource11BrowserComponent } from './resource-11-browser';

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
}
