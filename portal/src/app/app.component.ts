import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { ThemeService } from './services/theme.service';
import { CrossTabAuthSyncService } from './auth/services/cross-tab-auth-sync.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet],
  template: '<router-outlet />',
})
export class AppComponent {
  // Instantiate ThemeService at startup so the persisted theme is applied.
  private readonly theme = inject(ThemeService);
  // Instantiate eagerly so the cross-tab `storage` listener is registered from the
  // first paint, not only once some other component happens to inject it.
  private readonly crossTabAuthSync = inject(CrossTabAuthSyncService);
}
