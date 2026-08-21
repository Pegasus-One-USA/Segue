const NEW11_ENABLED_COOKIE = 'hb_new11_enabled';

/** Reads the New 11 feature-flag cookie set by Admin Settings' Default tab (see AdminSettingsComponent's
 *  new11Enabled checkbox) — every role's "Default | New 11" shell reads this once at construction to decide
 *  whether the New 11 tab (and its curated _11 data) is shown at all. No cookie (first-ever visit, or an Admin
 *  who never touched the checkbox) defaults to disabled. */
export function isNew11Enabled(): boolean {
  return document.cookie
    .split('; ')
    .some((entry) => entry === `${NEW11_ENABLED_COOKIE}=1`);
}

/** Writes the New 11 feature-flag cookie — called from AdminSettingsComponent's checkbox handler. A 1-year expiry
 *  and Path=/ so every role's page (same origin, different route) can read it, and it survives across logins/
 *  logouts within the same browser (the whole point — Admin toggles it once, then switches roles to verify). */
export function setNew11Enabled(enabled: boolean): void {
  const oneYearSeconds = 60 * 60 * 24 * 365;
  document.cookie = `${NEW11_ENABLED_COOKIE}=${enabled ? '1' : '0'}; Path=/; Max-Age=${oneYearSeconds}; SameSite=Lax`;
}
