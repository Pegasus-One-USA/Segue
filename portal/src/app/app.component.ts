import { Component, inject } from '@angular/core';
import { Router, RouterOutlet, NavigationStart, NavigationEnd, NavigationCancel, NavigationError, NavigationSkipped } from '@angular/router';
import { ThemeService } from './services/theme.service';
import { CrossTabAuthSyncService } from './auth/services/cross-tab-auth-sync.service';
import { BrandingService } from './services/branding.service';
import { ToastComponent } from './components/shared/toast/toast.component';
import { GlobalLoaderComponent } from './components/shared/global-loader/global-loader.component';
import { LoadingService } from './services/loading.service';
import { BlockingConfirmService } from './core/services/blocking-confirm.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, ToastComponent, GlobalLoaderComponent],
  // <app-toast />/<app-global-loader /> are mounted once here at the root so both are available on
  // every route (including /auth/login) — the app's single notification surface and single
  // "the app is busy" indicator, covering every HTTP request (loadingInterceptor) and every route
  // navigation (this component, below) — lazy chunks, guards, and resolvers included.
  //
  // The routed content is wrapped in its own element (router-outlet has no host element of its own to
  // attach this to) so it can be marked [inert] while the app is busy: [inert] removes the whole
  // subtree from the tab order and from hit-testing, which is what actually stops a keyboard user from
  // reaching a control behind the loader — GlobalLoaderComponent's backdrop only stops the mouse. Both
  // <app-toast /> and <app-global-loader /> stay outside this wrapper, deliberately: a toast must stay
  // dismissible and the loader itself must never be the thing it's blocking.
  template: `
    <div [attr.inert]="loading.isLoading() && !blockingConfirm.isOpen() ? '' : null">
      <router-outlet />
    </div>
    <app-toast />
    <app-global-loader />
  `,
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
  // protected (not private): read from the inline template above to drive [attr.inert].
  protected readonly loading = inject(LoadingService);
  // Also drives [attr.inert] above: a route's own in-template CanDeactivate confirm lives INSIDE the
  // routed subtree, and the navigation it is gating keeps LoadingService busy the whole time it is
  // open — so without this exception the dialog would inert its own buttons and deadlock navigation.
  // See BlockingConfirmService for the full explanation.
  protected readonly blockingConfirm = inject(BlockingConfirmService);

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
