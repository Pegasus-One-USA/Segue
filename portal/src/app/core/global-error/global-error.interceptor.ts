import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { StandardErrorResponse } from '../../operations/models/operations.model';
import { GlobalErrorDialogService } from './global-error-dialog.service';

/**
 * Phase 6A – Global Exception Management UI seam. When the backend returns a standardized error response
 * (one carrying an `errorReferenceId`, produced by the server's Global Exception Manager), this shows the
 * friendly dialog with the user-facing message and the quotable reference id. It never inspects or displays
 * technical detail.
 *
 * It only fires when a reference id is present, so routine validation errors (which components surface
 * inline) and 401s (handled by authInterceptor's refresh flow) don't trigger a dialog. The error is always
 * re-thrown so existing per-call error handling continues to run unchanged.
 */
export const globalErrorInterceptor: HttpInterceptorFn = (req, next) => {
  const dialogService = inject(GlobalErrorDialogService);

  return next(req).pipe(
    catchError((err: unknown) => {
      if (err instanceof HttpErrorResponse && err.status !== 401) {
        const body = err.error as StandardErrorResponse | null | undefined;
        const referenceId = body?.errorReferenceId;
        if (referenceId) {
          dialogService.show({
            message: body?.message ?? body?.error ?? 'An unexpected error occurred while processing your request.',
            referenceId,
            category: body?.category ?? null,
          });
        }
      }
      return throwError(() => err);
    }),
  );
};
