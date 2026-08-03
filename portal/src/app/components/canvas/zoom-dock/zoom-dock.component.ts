import { Component, input, output } from '@angular/core';

@Component({
  selector: 'app-zoom-dock',
  standalone: true,
  imports: [],
  templateUrl: './zoom-dock.component.html',
  styleUrl: './zoom-dock.component.scss',
})
export class ZoomDockComponent {
  readonly zoomPercent = input('100%');
  /** Shows a 5th "fit to view" button — off by default so existing dock usages are unaffected. */
  readonly showFit     = input(false);
  readonly zoomIn      = output<void>();
  readonly zoomOut     = output<void>();
  readonly reset       = output<void>();
  readonly fit         = output<void>();
}
