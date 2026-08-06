import { AUTH_EMAIL_STORAGE_KEY } from '../../../../core/routes';

// Same storage key + userIdentity scoping as LaunchStandaloneProviderComponent's own private sessionId
// getter (see that component's SESSION_ID_STORAGE_KEY remarks) — duplicated here (not imported) so this stays a
// plain read with no dependency on that component's lifecycle. Lets a sibling feature (New 11's Import
// Practitioners) send the same value as FHIRBridge's callerId parameter and reuse that session's already-
// authorized Provider Standalone token, instead of the request carrying none and finding nothing cached.
const SESSION_ID_STORAGE_KEY = 'providerStandaloneSessionId';

export function readProviderStandaloneSessionId(): string | null {
  let identity: string | null = null;
  try {
    identity = sessionStorage.getItem(AUTH_EMAIL_STORAGE_KEY);
  } catch {
    return null;
  }
  const key = identity ? `${SESSION_ID_STORAGE_KEY}:${identity}` : SESSION_ID_STORAGE_KEY;
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}
