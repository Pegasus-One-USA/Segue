import { test, expect } from '@playwright/test';
import { EhrEndpointsPage } from '../pages/ehr-endpoints.page';
import { EhrEndpointDialog } from '../pages/ehr-endpoint-dialog.page';
import { uniqueName, env } from '../fixtures/env';
import { deleteE2eEndpoints } from '../fixtures/cleanup';

/**
 * Server-side validation, exercised through the UI.
 *
 * These differ from ehr-endpoints-validation.spec.ts in where the rejection comes from: there the
 * Angular form blocks the request, here the request IS sent and the API rejects it. Both matter,
 * because the client rules and the server rules are maintained separately and can drift apart.
 *
 * To reach the server, each test must first get past the client validators -- so it fills a valid
 * form, then defeats one client rule (whitespace satisfies Validators.required but not the
 * server's IsNullOrWhiteSpace check) so the request actually goes out.
 *
 * KNOWN GAP, asserted as-is below: the dialog reads err.error?.title, but this API returns its
 * message under `error`/`message` and never sets `title` (see the error shape built in
 * Program.cs). So the specific server reason -- "Endpoint name is required." -- is dropped and
 * the user sees the generic "Failed to save endpoint. Please try again." instead. These tests
 * pin the CURRENT behaviour: the save is correctly refused and the dialog stays open. When the
 * dialog is fixed to read `error`/`message`, tighten the assertions here to the specific text.
 */

test.afterAll(async ({ playwright }) => {
  const request = await playwright.request.newContext({
    baseURL: env.apiUrl,
    storageState: '.auth/user.json',
  });
  try {
    await deleteE2eEndpoints(request);
  } finally {
    await request.dispose();
  }
});

test.describe('EHR Endpoints — server-side validation', () => {
  test('a whitespace-only name is rejected by the API and shown in the dialog', async ({ page }) => {
    const list = new EhrEndpointsPage(page);
    const dialog = new EhrEndpointDialog(page);
    const name = uniqueName('server-blank-name');

    await list.open();
    await list.addButton.click();
    await dialog.expectOpen('Add');

    await dialog.fill({
      vendor: 'GenericFhir',
      endpointType: 'MyChart',
      vendorEndpointId: `${name}-id`,
      name,
      fhirBaseUrl: 'https://e2e.example.org/api/FHIR/R4/',
      formatType: 'R4',
      status: 'active',
    });

    // Angular's Validators.required treats "   " as present, so this passes the client gate and
    // reaches the server -- which rejects it via EhrEndpointService.ValidateRequest.
    await page.locator('#ehr-name').fill('   ');

    const response = page.waitForResponse(
      r => r.url().includes('/api/v1/ehr-endpoints') && r.request().method() === 'POST'
    );
    await dialog.saveButton.click();
    expect((await response).status()).toBe(400);

    // The dialog stays open and surfaces the server's message rather than closing silently.
    await expect(dialog.saveButton).toBeVisible();
    await expect(dialog.error).toBeVisible();
    // See KNOWN GAP above: the server's "Endpoint name is required." is not surfaced today.
    await expect(dialog.error).toContainText(/failed to save endpoint/i);
  });

  test('the API rejects a blank name even when the client control is bypassed', async ({ page }) => {
    const list = new EhrEndpointsPage(page);
    const dialog = new EhrEndpointDialog(page);
    const name = uniqueName('server-bypass');

    await list.open();
    await list.addButton.click();
    await dialog.expectOpen('Add');

    await dialog.fill({
      vendor: 'GenericFhir',
      endpointType: 'MyChart',
      vendorEndpointId: `${name}-id`,
      name,
      fhirBaseUrl: 'https://e2e.example.org/api/FHIR/R4/',
      formatType: 'R4',
      status: 'active',
    });

    // Blank the Status field the same way -- whitespace satisfies the client, not the server.
    await page.locator('#ehr-status').fill(' ');

    const response = page.waitForResponse(
      r => r.url().includes('/api/v1/ehr-endpoints') && r.request().method() === 'POST'
    );
    await dialog.saveButton.click();
    expect((await response).status()).toBe(400);
    await expect(dialog.error).toBeVisible();

    // Nothing was persisted.
    await dialog.close();
    await list.search(name);
    await expect(list.row(name)).toHaveCount(0);
  });

  test('a valid endpoint still saves — the server gate does not block good input', async ({ page }) => {
    const list = new EhrEndpointsPage(page);
    const dialog = new EhrEndpointDialog(page);
    const name = uniqueName('server-happy');

    await list.open();
    await list.addButton.click();
    await dialog.expectOpen('Add');

    await dialog.fill({
      vendor: 'GenericFhir',
      endpointType: 'MyChart',
      vendorEndpointId: `${name}-id`,
      name,
      fhirBaseUrl: 'https://e2e.example.org/api/FHIR/R4/',
      formatType: 'R4',
      status: 'active',
    });

    const response = page.waitForResponse(
      r => r.url().includes('/api/v1/ehr-endpoints') && r.request().method() === 'POST'
    );
    await dialog.saveButton.click();
    expect((await response).status()).toBeLessThan(300);

    await list.search(name);
    await expect(list.row(name)).toBeVisible();
  });
});
