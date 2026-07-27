import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { finalize } from 'rxjs';
import { LoadingService } from '../services/loading.service';

/**
 * Marks every outgoing HTTP request as "in flight" on the shared LoadingService so
 * GlobalLoaderComponent can show a single app-wide indicator without any component
 * having to wire up its own loading state for the common case. A component that
 * wants its own local spinner/disabled-button feedback for one specific call still
 * can — this is purely additive, ambient feedback for everything else.
 */
export const loadingInterceptor: HttpInterceptorFn = (req, next) => {
  const loading = inject(LoadingService);
  loading.start();
  return next(req).pipe(finalize(() => loading.stop()));
};
