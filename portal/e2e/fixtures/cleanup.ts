import { APIRequestContext } from '@playwright/test';
import { env, E2E_PREFIX } from './env';

/**
 * The API guards cookie-authenticated state-changing requests with a double-submit CSRF check:
 * the X-CSRF-Token header must equal the fhirbridge_csrf cookie, or the request is 403'd (see
 * the CSRF middleware in Program.cs). GET is unaffected, which is why listing works without it
 * but DELETE does not.
 */
async function csrfHeader(request: APIRequestContext): Promise<Record<string, string>> {
  const state = await request.storageState();
  const token = state.cookies.find(c => c.name === 'fhirbridge_csrf')?.value;
  if (!token) {
    throw new Error(
      'No fhirbridge_csrf cookie in the saved session; cannot issue state-changing API calls.'
    );
  }
  return { 'X-CSRF-Token': token };
}

interface EndpointRow {
  id: string;
  name?: string;
  vendorEndpointId?: string;
}

/**
 * Deletes every EHR endpoint this suite created.
 *
 * These specs run against FHIRBridge_v2 -- the shared local dev database -- so anything left
 * behind pollutes real development data. Cleanup goes through the API rather than the UI so it
 * still works after a spec that failed halfway with a dialog open.
 *
 * Auth is by session COOKIE, not a bearer token: the API strips raw tokens from the login
 * response and sets them as HttpOnly cookies instead, so an Authorization header gets a 401.
 * The APIRequestContext must therefore be built from the saved storageState.
 *
 * Only rows whose name or vendorEndpointId carries E2E_PREFIX are ever deleted -- the ~480
 * seeded Epic/MyChart rows are never touched. Throws rather than returning quietly on failure:
 * silent cleanup failures let junk accumulate invisibly, which is how the dev database ends up
 * full of test rows.
 */
export async function deleteE2eEndpoints(request: APIRequestContext): Promise<number> {
  // The server-side search covers name and FHIR base URL. Page through the full listing instead
  // so rows that carry the prefix only on vendorEndpointId are caught as well.
  const res = await request.get(
    `${env.apiUrl}/api/v1/ehr-endpoints/paged?page=1&pageSize=1000`
  );
  if (!res.ok()) {
    throw new Error(
      `E2E cleanup could not list endpoints: HTTP ${res.status()}. ` +
        `Test rows may be left in the database.`
    );
  }

  const body = await res.json();
  const items: EndpointRow[] = body.items ?? [];

  const mine = items.filter(
    row =>
      row.name?.startsWith(E2E_PREFIX) || row.vendorEndpointId?.startsWith(E2E_PREFIX)
  );

  const headers = await csrfHeader(request);
  const failed: string[] = [];
  let deleted = 0;
  for (const row of mine) {
    const del = await request.delete(`${env.apiUrl}/api/v1/ehr-endpoints/${row.id}`, { headers });
    if (del.ok()) deleted++;
    else failed.push(`${row.name ?? row.id} (HTTP ${del.status()})`);
  }

  if (failed.length) {
    throw new Error(`E2E cleanup failed to delete ${failed.length} row(s): ${failed.join(', ')}`);
  }
  return deleted;
}

/**
 * Deletes every source connection this suite created (name starts with E2E_PREFIX).
 *
 * Same constraints as the endpoint cleanup above: cookie session, CSRF header on the delete, and
 * it never touches a row it did not create.
 */
export async function deleteE2eSourceConnections(request: APIRequestContext): Promise<number> {
  const res = await request.get(`${env.apiUrl}/api/v1/source-connections`);
  if (!res.ok()) {
    throw new Error(`E2E cleanup could not list source connections: HTTP ${res.status()}.`);
  }

  const body = await res.json();
  const items: Array<{ id: string; name?: string }> = Array.isArray(body) ? body : (body.items ?? []);
  const mine = items.filter(row => row.name?.startsWith(E2E_PREFIX));

  const headers = await csrfHeader(request);
  const failed: string[] = [];
  let deleted = 0;
  for (const row of mine) {
    const del = await request.delete(`${env.apiUrl}/api/v1/source-connections/${row.id}`, { headers });
    if (del.ok()) deleted++;
    else failed.push(`${row.name ?? row.id} (HTTP ${del.status()})`);
  }

  if (failed.length) {
    throw new Error(`E2E cleanup failed to delete ${failed.length} source connection(s): ${failed.join(', ')}`);
  }
  return deleted;
}
