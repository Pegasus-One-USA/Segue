import { Component, input, output } from '@angular/core';

@Component({
  selector: 'app-node-tools',
  standalone: true,
  imports: [],
  templateUrl: './node-tools.component.html',
  styleUrl: './node-tools.component.scss',
})
export class NodeToolsComponent {
  readonly showConfigure = input(false);
  readonly plusEnabled   = input(true);

  readonly configure = output<void>();
  readonly addNext   = output<void>();
  readonly delete    = output<void>();
}
