import { Component, inject } from '@angular/core';
import { Router, RouterOutlet, NavigationStart, NavigationEnd, NavigationCancel, NavigationError, NavigationSkipped } from '@angular/router';
import { ThemeService } from './services/theme.service';
import { CrossTabAuthSyncService } from './auth/services/cross-tab-auth-sync.service';
import { BrandingService } from './services/branding.service';
import { ToastComponent } from './components/shared/toast/toast.component';
import { GlobalLoaderComponent } from './components/shared/global-loader/global-loader.component';
import { LoadingService } from './services/loading.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, ToastComponent, GlobalLoaderComponent],
  // <app-toast />/<app-global-loader /> are mounted once here at the root so both are available on
  // every route (including /auth/login) — the app's single notification surface and single
  // "the app is busy" indicator, covering every HTTP request (loadingInterceptor) and every route
  // navigation (this component, below) — lazy chunks, guards, and resolvers included.
  template: '<router-outlet /><app-toast /><app-global-loader />',
})
export class AppComponent {
  // Instantiate ThemeService at startup so the persisted theme is applied.
  private readonly theme = inject(ThemeService);
  // Instantiate eagerly so the cross-tab `storage` listener is registered from the
  // first paint, not only once some other component happens to inject it.
  private readonly crossTabAuthSync = inject(CrossTabAuthSyncService);
  // BrandingService's actual resolution now happens in app.config.ts's APP_INITIALIZER (awaited
  // before the router renders anything) — this reference just keeps the singleton reachable here
  // for consistency with the other startup services on this line; it triggers nothing on its own.
  private readonly branding = inject(BrandingService);
  private readonly loading = inject(LoadingService);

  constructor() {
    // Router navigations are a second, non-HTTP source of "the app is busy" — a lazy-loaded
    // feature module's chunk fetch, or a guard/resolver doing async work, wouldn't otherwise
    // show anything. NavigationStart always pairs with exactly one of End/Cancel/Error/Skipped —
    // missing any of the four would leave the counter stuck incremented forever.
    inject(Router).events.subscribe(e => {
      if (e instanceof NavigationStart) {
        this.loading.start();
      } else if (
        e instanceof NavigationEnd ||
        e instanceof NavigationCancel ||
        e instanceof NavigationError ||
        e instanceof NavigationSkipped
      ) {
        this.loading.stop();
      }
    });
  }
}
