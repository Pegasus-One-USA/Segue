/**
 * Best-effort extraction of a user-facing message from a failed HTTP call.
 * The backend's exception middleware responds with `{ error: "..." }` (see Program.cs's
 * MapException) — Angular's own HttpErrorResponse.message is always a generic string like
 * "Http failure response for .../foo: 400 Bad Request", so it must never be checked first,
 * or the real backend message (and any logic keyed off its wording) never gets seen.
 */
export function extractApiErrorMessage(err: unknown, fallback: string): string {
  const httpErr = err as { error?: { error?: string }; message?: string } | null;
  return httpErr?.error?.error ?? httpErr?.message ?? fallback;
}
