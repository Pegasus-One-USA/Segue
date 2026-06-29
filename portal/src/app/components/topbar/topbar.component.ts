import { Component, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';

@Component({
  selector: 'app-topbar',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './topbar.component.html',
  styleUrl: './topbar.component.scss',
})
export class TopbarComponent {
  readonly scenarioName = input('');
  readonly nameChange   = output<string>();
  readonly preview      = output<void>();
  readonly reset        = output<void>();
}
