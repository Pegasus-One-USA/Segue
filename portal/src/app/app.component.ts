import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { ThemeService } from './services/theme.service';
import { CrossTabAuthSyncService } from './auth/services/cross-tab-auth-sync.service';
import { BrandingService } from './services/branding.service';
import { ToastComponent } from './components/shared/toast/toast.component';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, ToastComponent],
  // <app-toast /> is mounted once here at the root so the single, branded toast is available on every
  // route (including /auth/login) — the app's single notification surface.
  template: '<router-outlet /><app-toast />',
})
export class AppComponent {
  // Instantiate ThemeService at startup so the persisted theme is applied.
  private readonly theme = inject(ThemeService);
  // Instantiate eagerly so the cross-tab `storage` listener is registered from the
  // first paint, not only once some other component happens to inject it.
  private readonly crossTabAuthSync = inject(CrossTabAuthSyncService);
  // Instantiate BrandingService at startup so tenant branding is resolved and
  // applied before the router renders anything (including /auth/login).
  private readonly branding = inject(BrandingService);
}
