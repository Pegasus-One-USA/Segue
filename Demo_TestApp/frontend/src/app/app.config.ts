import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZoneChangeDetection } from '@angular/core';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { provideHttpClient, withFetch } from '@angular/common/http';
import { provideRouter, withEnabledBlockingInitialNavigation } from '@angular/router';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZoneChangeDetection({ eventCoalescing: true }),
    // Blocking initial navigation, since this app has no <router-outlet> (see app.routes.ts) to naturally gate
    // component creation on route resolution. patient-standalone/provider-standalone still read query params via
    // ActivatedRoute; demo-type-2 (App, LaunchProviderInAppComponent, PatientService) reads window.location.search
    // directly instead, since ActivatedRoute.snapshot wasn't reliably populated by the time their code ran even
    // with this flag on.
    provideRouter(routes, withEnabledBlockingInitialNavigation()),
    provideAnimationsAsync(),
    provideHttpClient(withFetch()),
  ]
};
