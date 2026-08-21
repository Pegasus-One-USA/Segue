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
  /** Corner of the host it docks to — defaults to the main workflow canvas's original bottom-left;
   *  the field-mapping canvas docks it top-right instead, alongside its own "+ Add a table" control.
   *  'inline' drops the fixed positioning entirely so it can sit as a normal flex item inside a
   *  toolbar row instead of floating over canvas content (see node-library-dialog's mapping toolbar). */
  readonly corner      = input<'bottom-left' | 'top-right' | 'inline'>('bottom-left');
  readonly zoomIn      = output<void>();
  readonly zoomOut     = output<void>();
  readonly reset       = output<void>();
  readonly fit         = output<void>();
}
