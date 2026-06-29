import { Component, input } from '@angular/core';

export type CalloutType = 'info' | 'warn' | 'tip';

@Component({
  selector: 'app-callout',
  standalone: true,
  imports: [],
  templateUrl: './callout.component.html',
  styleUrl: './callout.component.scss',
})
export class CalloutComponent {
  readonly type = input<CalloutType>('info');
  readonly icon = input('ℹ');
}
