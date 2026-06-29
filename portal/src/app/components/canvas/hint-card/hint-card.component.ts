import { Component, signal } from '@angular/core';

@Component({
  selector: 'app-hint-card',
  standalone: true,
  imports: [],
  templateUrl: './hint-card.component.html',
  styleUrl: './hint-card.component.scss',
})
export class HintCardComponent {
  readonly visible = signal(true);
}
