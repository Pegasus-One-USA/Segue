/**
 * Test configuration, read from the gitignored .env.e2e at the repo root.
 *
 * Every value is required and has NO fallback: a missing credential fails the run immediately
 * with a clear message rather than silently defaulting to something that half-works and
 * produces a confusing failure twenty steps later.
 */

function required(name: string): string {
  const value = process.env[name];
  if (!value || !value.trim()) {
    throw new Error(
      `Missing ${name}. Add it to .env.e2e at the repo root (see portal/e2e/README.md). ` +
        `That file is gitignored and must never be committed.`
    );
  }
  return value;
}

export const env = {
  get adminEmail(): string {
    return required('E2E_ADMIN_EMAIL');
  },
  get adminPassword(): string {
    return required('E2E_ADMIN_PASSWORD');
  },
  get baseUrl(): string {
    return process.env.E2E_BASE_URL ?? 'http://localhost:4200';
  },
  get apiUrl(): string {
    return process.env.E2E_API_URL ?? 'http://localhost:5000';
  },

  // ── Epic sandbox, Backend System (SMART Backend Services) ───────────────────
  // Typed into the source-connection wizard exactly as a user would. The suite also lets the app
  // make its REAL calls out to Epic with these (discovery, private_key_jwt token exchange), so
  // these specs depend on Epic's sandbox being reachable.
  get epicClientId(): string {
    return required('E2E_EPIC_CLIENT_ID');
  },
  get epicPrivateKeyPath(): string {
    return required('E2E_EPIC_PRIVATE_KEY_PATH');
  },
  /**
   * The kid and JWKS URL actually registered with Epic for this client id.
   *
   * Importing a key mints a BRAND-NEW kid every time and points the JWKS URL at this instance,
   * neither of which Epic knows about -- so a connection left on the generated values cannot
   * authenticate. The real flow overrides both by hand after importing, which is what the spec
   * does.
   */
  get epicKeyId(): string {
    return required('E2E_EPIC_KEY_ID');
  },
  get epicJwksUrl(): string {
    return required('E2E_EPIC_JWKS_URL');
  },
  get epicFhirBaseUrl(): string {
    return (
      process.env.E2E_EPIC_FHIR_BASE_URL ??
      'https://fhir.epic.com/interconnect-fhir-oauth/api/FHIR/R4'
    );
  },
};

/**
 * Prefix for every row these tests create in the shared dev database.
 *
 * These specs run against FHIRBridge_v2 -- the same database used for local development -- so
 * they must be able to find their own rows among the ~480 seeded EHR endpoints, and must never
 * touch anything they did not create. Every created row carries this prefix, and teardown only
 * ever deletes rows matching it.
 */
export const E2E_PREFIX = 'e2e-test';

/** A unique, identifiable name for one test run's row. */
export function uniqueName(label: string): string {
  const stamp = `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 7)}`;
  return `${E2E_PREFIX}-${label}-${stamp}`;
}
