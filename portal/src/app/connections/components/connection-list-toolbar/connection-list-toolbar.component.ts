import { Component, input, output } from '@angular/core';

/**
 * The bar above a connection list: search box, the page's own filters (projected), Reset and New. Search runs in the
 * browser over rows already loaded, so it sends no request and needs no spinner.
 */
@Component({
  selector: 'app-connection-list-toolbar',
  standalone: true,
  templateUrl: './connection-list-toolbar.component.html',
  styleUrl: './connection-list-toolbar.component.scss',
})
export class ConnectionListToolbarComponent {
  readonly search = input('');
  readonly placeholder = input('Search…');
  /** 'src' or 'dest' — prefixes the e2e test ids. */
  readonly testIdPrefix = input.required<string>();
  readonly canCreate = input(false);

  readonly searchChange = output<string>();
  readonly resetClicked = output<void>();
  readonly createClicked = output<void>();
}
