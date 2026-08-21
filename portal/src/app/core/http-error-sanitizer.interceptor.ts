import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';
import { resolveFallbackMessage } from './error-messages.catalog';

/**
 * Reads whichever safe, human-readable field the backend actually populated for this error shape.
 * `error`/`message` come from the global exception handler and the webhook/OAuth minimal APIs;
 * `title` comes from ASP.NET Core's automatic ProblemDetails validation-error responses;
 * `error_description` comes from the OAuth-shaped workflow endpoints. All four are backend-authored
 * and — after the FHIRBridgeException.UserMessage / NotFoundException fixes — safe to show verbatim.
 */
function extractBackendMessage(body: unknown): string | undefined {
  if (!body || typeof body !== 'object') {
    return undefined;
  }
  const b = body as Record<string, unknown>;
  const candidate = b['error'] ?? b['message'] ?? b['title'] ?? b['error_description'];
  if (typeof candidate !== 'string' || candidate.trim().length === 0) {
    return undefined;
  }
  return isDisplaySafe(candidate) ? candidate : undefined;
}

/**
 * Defense-in-depth guard: a backend message is shown verbatim only if it looks like a short, human-written
 * sentence. Markup (an HTML/XML error page), multi-line/stack output, or an oversized payload is rejected so a
 * leak from any endpoint that bypasses the global handler still can't render raw HTML/technical text in the UI —
 * the caller falls back to a generic status message instead. Mirrors the backend's ClientSafeMessage guard.
 */
function isDisplaySafe(message: string): boolean {
  if (message.length > 300) return false;
  if (/[<>]/.test(message)) return false;
  if (/[\r\n]/.test(message)) return false;
  if (/exception/i.test(message)) return false;
  if (message.includes('   at ')) return false;
  return true;
}

/**
 * The ONE place in the app allowed to read Angular's synthetic `HttpErrorResponse.message`. That
 * field is always a generic string of the form "Http failure response for
 * http://localhost:5000/api/...: 401 Unauthorized" — it leaks the API host, port, endpoint path, and
 * status to anything that reads it, and a lot of existing (and future) code reaches for `err?.message`
 * as a fallback. This interceptor replaces `.message` on every error response with either the
 * backend's own safe text or a generic status-driven fallback, so every `err.message` read anywhere —
 * present or future — is safe by construction. No component needs to know this interceptor exists.
 *
 * `.error` (the raw backend body) and `.status` are left completely untouched — code that reads
 * structured fields explicitly (e.g. `err.error.error`) sees exactly what the backend sent; only the
 * synthetic `.message` string is sanitized.
 */
export const httpErrorSanitizerInterceptor: HttpInterceptorFn = (req, next) =>
  next(req).pipe(
    catchError((err: unknown) => {
      if (!(err instanceof HttpErrorResponse)) {
        return throwError(() => err);
      }

      const safeMessage = extractBackendMessage(err.error) ?? resolveFallbackMessage(err.status);

      const sanitized = new HttpErrorResponse({
        error: err.error,
        headers: err.headers,
        status: err.status,
        statusText: err.statusText,
        url: err.url ?? undefined,
      });
      // HttpErrorResponse.message is `readonly` at the type level only (TypeScript, not the runtime) —
      // Angular's own constructor always recomputes it from status/url, so it must be overwritten here.
      (sanitized as unknown as { message: string }).message = safeMessage;

      return throwError(() => sanitized);
    }),
  );
