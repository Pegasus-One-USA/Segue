import { test, expect } from '@playwright/test';
import { SourceConnectionsPage } from '../pages/source-connections.page';
import { EpicBackendForm } from '../pages/epic-backend-form.page';
import { uniqueName, env } from '../fixtures/env';
import { deleteE2eSourceConnections } from '../fixtures/cleanup';

/**
 * Source Connection master — Epic, Backend System audience.
 *
 * Driven exactly as a user drives it on screen, including the form's REAL outbound calls to Epic:
 * Discover fetches /.well-known/smart-configuration from fhir.epic.com, and the granted-scopes
 * check performs an actual client_credentials + private_key_jwt token exchange signed with the
 * imported key. Nothing is stubbed.
 *
 * That means these specs depend on Epic's sandbox being reachable. A vendor outage will fail them
 * -- deliberately, since the point is to prove the live integration works. Keep them out of any
 * fast/offline suite.
 *
 * Credentials come from .env.e2e (sandbox-only, gitignored). This drives the v1 form
 * (components/shared/ehr-vendor-source-form), which is what this master screen imports; the
 * workflow builder uses a separate v2 copy.
 */

// A live Epic round-trip plus a key import is well past the default per-test budget.
test.describe.configure({ timeout: 180_000 });

test.afterAll(async ({ playwright }) => {
  const request = await playwright.request.newContext({
    baseURL: env.apiUrl,
    storageState: '.auth/user.json',
  });
  try {
    const removed = await deleteE2eSourceConnections(request);
    if (removed) console.log(`[cleanup] removed ${removed} e2e source connection(s)`);
  } finally {
    await request.dispose();
  }
});

test.describe('Source Connection — Epic / Backend System', () => {
  test('the JWT signing-key fields belong to Backend System and no other audience', async ({ page }) => {
    const list = new SourceConnectionsPage(page);
    const form = new EpicBackendForm(page);

    await list.open();
    await list.startCreate('Epic');
    await form.expectOpen();

    // SMART Backend Services authenticates with private_key_jwt, so it -- and only it -- needs a
    // JWKS URL and a signing key. The interactive audiences use an authorization code instead.
    //
    // Asserting the DIFFERENCE between audiences rather than merely "these ids are present" is
    // deliberate: several interactive-only fields (callback/launch URL) are currently hidden for
    // every audience by a hardcoded showApplicationUrlsSection = false, so asserting their
    // absence under Backend System passes trivially and proves nothing.
    const JWT_FIELDS = ['eaf-jwksUrl', 'eaf-keySource', 'eaf-jwtKid', 'eaf-privateKeyRef', 'eaf-privateKeySecretName'];

    await form.selectBackendSystem();
    const backend = await form.visibleFieldIds();
    for (const id of JWT_FIELDS) expect(backend, `${id} should show for Backend System`).toContain(id);

    await form.audience.selectOption('provider-standalone');
    await page.waitForTimeout(500);
    const standalone = await form.visibleFieldIds();
    for (const id of JWT_FIELDS) expect(standalone, `${id} must not show for Provider Standalone`).not.toContain(id);

    await form.audience.selectOption('provider-ehr-launch');
    await page.waitForTimeout(500);
    const ehrLaunch = await form.visibleFieldIds();
    for (const id of JWT_FIELDS) expect(ehrLaunch, `${id} must not show for EHR Launch`).not.toContain(id);
  });

  test('Discover pulls Epic\'s real OAuth endpoints into the form', async ({ page }) => {
    const list = new SourceConnectionsPage(page);
    const form = new EpicBackendForm(page);

    await list.open();
    await list.startCreate('Epic');
    await form.selectBackendSystem();
    await form.baseUrl.fill(env.epicFhirBaseUrl);

    await expect(form.tokenEndpoint).toHaveValue('');
    await form.discover();

    // These are Epic's own published endpoints -- assert the real values, not merely "not empty",
    // so a discovery that silently returns something else fails here.
    await expect(form.tokenEndpoint).toHaveValue(/fhir\.epic\.com\/.*\/oauth2\/token/);
    await expect(form.authzEndpoint).toHaveValue(/fhir\.epic\.com\/.*\/oauth2\/authorize/);
  });

  test('importing the real private key provisions a signing key and fills its references', async ({ page }) => {
    const list = new SourceConnectionsPage(page);
    const form = new EpicBackendForm(page);

    await list.open();
    await list.startCreate('Epic');
    await form.selectBackendSystem();
    await form.importPrivateKey();

    // The server generates a kid for the imported key and hands back its vault coordinates.
    await expect(form.jwtKid).toHaveValue(/^fb-[0-9a-f]+$/);
    await expect(form.privateKeyRef).not.toHaveValue('');
    await expect(form.privateKeySecretName).not.toHaveValue('');
  });

  test('creates an Epic Backend System connection end to end and it persists', async ({ page }) => {
    const list = new SourceConnectionsPage(page);
    const form = new EpicBackendForm(page);
    const name = uniqueName('epic-backend');

    await list.open();
    await list.startCreate('Epic');
    await form.fillBackendSystem(name);
    await form.save();

    await list.search(name);
    await expect(list.row(name)).toBeVisible();

    // Reopen it: the audience must have round-tripped as Backend System, not silently defaulted.
    await list.rowAction(name, 'edit');
    await expect(form.audience).toHaveValue('backend-system');
    await expect(form.clientId).toHaveValue(env.epicClientId);
    await expect(form.tokenEndpoint).toHaveValue(/fhir\.epic\.com/);
  });

  test('the saved connection keeps the kid registered with Epic, not the one Import generated', async ({ page }) => {
    const list = new SourceConnectionsPage(page);
    const form = new EpicBackendForm(page);
    const name = uniqueName('epic-kid');

    await list.open();
    await list.startCreate('Epic');
    await form.appName.fill(name);
    await form.selectBackendSystem();
    await form.environment.selectOption('sandbox');
    await form.importPrivateKey();

    // Import always mints a FRESH kid and points the JWKS URL at this instance -- Epic knows
    // neither, so a connection left on these values cannot authenticate. Capture the generated
    // value so the assertion below is about the override taking effect, not merely about some
    // value being present.
    const generatedKid = await form.jwtKid.inputValue();
    expect(generatedKid).toMatch(/^fb-[0-9a-f]+$/);
    expect(generatedKid).not.toBe(env.epicKeyId);

    await form.applyRegisteredKeyIdentity();
    await form.clientId.fill(env.epicClientId);
    await form.baseUrl.fill(env.epicFhirBaseUrl);
    await form.discover();
    await form.save();

    // What matters is that the manual override is what persisted -- not the generated kid.
    // Reopened, the key renders read-only (see existingKeyId) rather than as an input.
    await list.search(name);
    await list.rowAction(name, 'edit');
    await expect(form.existingKeyId).toHaveText(env.epicKeyId);
    await expect(form.jwksUrl).toHaveValue(env.epicJwksUrl);
  });

  test('an incomplete Backend System form is refused and says what is missing', async ({ page }) => {
    const list = new SourceConnectionsPage(page);
    const form = new EpicBackendForm(page);

    await list.open();
    await list.startCreate('Epic');
    await form.selectBackendSystem();
    await form.appName.fill(uniqueName('epic-incomplete'));

    // No base URL, no client id, no signing key -- Save must not write.
    const writes: string[] = [];
    page.on('request', r => {
      if (r.method() === 'POST' && r.url().includes('/api/v1/source-connections') &&
          !r.url().includes('import-signing-key')) {
        writes.push(r.url());
      }
    });

    await expect(form.missingHint).toBeVisible();
    await form.saveExpectingRefusal();
    expect(writes, 'an incomplete form must not create a connection').toEqual([]);
  });

  test('deletes an Epic Backend System connection', async ({ page }) => {
    const list = new SourceConnectionsPage(page);
    const form = new EpicBackendForm(page);
    const name = uniqueName('epic-delete');

    await list.open();
    await list.startCreate('Epic');
    await form.fillBackendSystem(name);
    await form.save();

    await list.search(name);
    await expect(list.row(name)).toBeVisible();

    await list.rowAction(name, 'delete');
    await page.getByTestId('confirm-accept').click();

    await list.search(name);
    await expect(list.row(name)).toHaveCount(0);
  });
});
