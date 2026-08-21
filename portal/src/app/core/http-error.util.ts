/**
 * Best-effort extraction of a user-facing message from a failed HTTP call.
 * Prefers the backend's own `{ error: "..." }` body (see Program.cs's MapException) so any
 * logic keyed off its specific wording still works. The `httpErr.message` fallback is safe to use
 * here — httpErrorSanitizerInterceptor (registered in app.config.ts) has already replaced Angular's
 * synthetic "Http failure response for .../foo: 400 Bad Request" string with safe, catalog-backed
 * text before this ever runs, for every HTTP call in the app.
 */
export function extractApiErrorMessage(err: unknown, fallback: string): string {
  const httpErr = err as { error?: { error?: string }; message?: string } | null;
  return httpErr?.error?.error ?? httpErr?.message ?? fallback;
}
