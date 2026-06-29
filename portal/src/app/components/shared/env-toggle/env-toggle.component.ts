import { Component, input, output } from '@angular/core';
import { EnvKey } from '../../../models/epic-env.model';

@Component({
  selector: 'app-env-toggle',
  standalone: true,
  imports: [],
  templateUrl: './env-toggle.component.html',
  styleUrl: './env-toggle.component.scss',
})
export class EnvToggleComponent {
  readonly value   = input<EnvKey>('sandbox');
  readonly changed = output<EnvKey>();

  select(env: EnvKey): void {
    this.changed.emit(env);
  }
}
