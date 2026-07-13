import { HttpErrorResponse } from '@angular/common/http';

/**
 * Extracts a user-facing message from a failed auth HTTP call.
 *
 * The backend's actual, currently-registered exception handler (the inline `UseExceptionHandler`
 * lambda in Program.cs — NOT the unused GlobalExceptionHandlingMiddleware class, which is dead
 * code and never wired into the pipeline) turns every handled exception into
 * `{ "error": "Invalid email or password." }` — a field called `error`, a plain string. A
 * separate piece of middleware (the password-change-required gate) instead returns
 * `{ "message": "..." }`. Neither ever sends `title`. Checking all three defensively means this
 * keeps working if either shape changes again without needing to re-derive it from scratch.
 *
 * status 0 means the request never got a response at all (server unreachable, CORS, a
 * protocol mismatch) — there is no backend message to read in that case, and showing Angular's
 * raw `HttpErrorResponse.message` ("Http failure response for ...: 0 Unknown Error") is not a
 * "wrong credentials"-shaped problem, so it gets its own message instead.
 */
export function authErrorMessage(err: unknown, fallback: string): string {
  if (err instanceof HttpErrorResponse) {
    if (err.status === 0) {
      return 'Unable to reach the server. Check your connection and try again.';
    }
    const body = err.error as { error?: string; message?: string; title?: string } | null;
    const reason = body?.error || body?.message || body?.title;
    if (reason) return reason;
  }
  return fallback;
}
