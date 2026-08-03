import { Injectable, signal, computed } from '@angular/core';
import { Observable } from 'rxjs';
import { finalize, tap } from 'rxjs/operators';

/**
 * Tracks every ambient, app-wide "something is loading" source — in-flight HTTP
 * requests (loadingInterceptor), route navigations (AppComponent, covering
 * lazy-loaded chunks/guards/resolvers), and any still-mocked service call wrapped
 * in track() below (e.g. BrandingService.save(), InvitationService — anything
 * simulating backend latency with of(...).pipe(delay(...)) instead of a real
 * HttpClient call, which loadingInterceptor has nothing to intercept) — behind
 * one counter, so a single global indicator (see GlobalLoaderComponent) can
 * reflect "the app is busy" without every component wiring up its own loading
 * state. A component that wants its own local spinner/disabled-button feedback
 * for one specific action should still use its own signal — this is purely
 * additive, ambient feedback for everything else.
 */
@Injectable({ providedIn: 'root' })
export class LoadingService {
  private readonly activeRequests = signal(0);

  readonly isLoading = computed(() => this.activeRequests() > 0);

  start(): void {
    this.activeRequests.update(n => n + 1);
  }

  stop(): void {
    this.activeRequests.update(n => Math.max(0, n - 1));
  }

  /** Wraps any Observable (typically a mocked service call with a fake delay) so it
   *  counts toward the global indicator the same way a real HTTP request would —
   *  start() on each subscription, stop() on completion/error/unsubscribe. */
  track<T>(source: Observable<T>): Observable<T> {
    return source.pipe(
      tap({ subscribe: () => this.start() }),
      finalize(() => this.stop()),
    );
  }
}
