import { HttpContextToken, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { finalize } from 'rxjs';
import { LoadingService } from '../services/loading.service';

/**
 * Opt-in per-request flag: set via `context: new HttpContext().set(SKIP_LOADER, true)` on a request
 * that should update its data silently in the background — a periodic auto-refresh or a push-driven
 * (e.g. SignalR-triggered) refetch, as opposed to something the user directly asked for or that's
 * populating a screen for the first time. Defaults to `false`, so every existing call site (and any
 * new one that doesn't know this token exists) keeps showing the global loader exactly as before —
 * this is additive, not a behavior change for anyone who doesn't explicitly opt in.
 */
export const SKIP_LOADER = new HttpContextToken<boolean>(() => false);

/**
 * Marks every outgoing HTTP request as "in flight" on the shared LoadingService so
 * GlobalLoaderComponent can show a single app-wide indicator without any component
 * having to wire up its own loading state for the common case. A component that
 * wants its own local spinner/disabled-button feedback for one specific call still
 * can — this is purely additive, ambient feedback for everything else.
 *
 * Requests carrying SKIP_LOADER=true bypass this entirely (not even started/stopped) — see the token's
 * own doc comment above for when that's appropriate. Skipping the counter altogether (rather than,
 * say, starting then immediately stopping) also means a silent request can never leave the counter
 * stuck if it fails or hangs — there's nothing for it to get stuck on in the first place.
 */
export const loadingInterceptor: HttpInterceptorFn = (req, next) => {
  if (req.context.get(SKIP_LOADER)) {
    return next(req);
  }

  const loading = inject(LoadingService);
  loading.start();
  return next(req).pipe(finalize(() => loading.stop()));
};
