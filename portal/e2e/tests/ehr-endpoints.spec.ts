import { test, expect } from '@playwright/test';
import { EhrEndpointsPage } from '../pages/ehr-endpoints.page';
import { EhrEndpointDialog } from '../pages/ehr-endpoint-dialog.page';
import { uniqueName, env } from '../fixtures/env';
import { deleteE2eEndpoints } from '../fixtures/cleanup';

/**
 * EHR Endpoints master-data CRUD.
 *
 * These run against the shared FHIRBridge_v2 dev database, which already holds ~480 seeded rows
 * (the Epic sandbox plus Epic's imported MyChart directory). Two consequences shape every test
 * here:
 *
 *   1. The list is never empty and is paginated, so a newly created row is NOT on screen. Every
 *      assertion searches for the row by its unique name first.
 *   2. Nothing seeded may be touched. Every row created carries the e2e- prefix, and cleanup only
 *      ever deletes rows matching it.
 */

test.afterAll(async ({ playwright }) => {
  // A fresh API context carrying the saved session cookies, so cleanup still runs after a spec
  // that aborted mid-flow. baseURL must be the API origin for the session cookies to be sent.
  const request = await playwright.request.newContext({
    baseURL: env.apiUrl,
    storageState: '.auth/user.json',
  });
  try {
    const removed = await deleteE2eEndpoints(request);
    if (removed) console.log(`[cleanup] removed ${removed} e2e endpoint row(s)`);
  } finally {
    await request.dispose();
  }
});

test.describe('EHR Endpoints', () => {
  test('creates an endpoint and finds it in the list', async ({ page }) => {
    const list = new EhrEndpointsPage(page);
    const dialog = new EhrEndpointDialog(page);
    const name = uniqueName('create');

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
    await dialog.save();

    // ~480 seeded rows means the new row is not on page 1 -- search for it.
    await list.search(name);
    await expect(list.row(name)).toBeVisible();
  });

  test('edits an endpoint and persists the change', async ({ page }) => {
    const list = new EhrEndpointsPage(page);
    const dialog = new EhrEndpointDialog(page);
    const name = uniqueName('edit');
    const updatedUrl = 'https://e2e-updated.example.org/api/FHIR/R4/';

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
    await dialog.save();

    await list.search(name);
    await list.rowAction(name, 'edit');
    await dialog.expectOpen('Edit');

    await dialog.fill({ fhirBaseUrl: updatedUrl });
    await dialog.save();

    // Reopen from a fresh search: proves the change round-tripped through the API, not just the
    // in-memory row the grid was holding.
    await list.search(name);
    await list.rowAction(name, 'edit');
    await dialog.expectOpen('Edit');
    expect((await dialog.readValues()).fhirBaseUrl).toBe(updatedUrl);
  });

  test('deletes an endpoint', async ({ page }) => {
    const list = new EhrEndpointsPage(page);
    const dialog = new EhrEndpointDialog(page);
    const name = uniqueName('delete');

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
    await dialog.save();

    await list.search(name);
    await expect(list.row(name)).toBeVisible();

    await list.rowAction(name, 'delete');
    await page.getByTestId('confirm-accept').click();

    await list.search(name);
    await expect(list.row(name)).toHaveCount(0);
  });

  test('search narrows the list to matching rows', async ({ page }) => {
    const list = new EhrEndpointsPage(page);
    const dialog = new EhrEndpointDialog(page);
    const name = uniqueName('search');

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
    await dialog.save();

    // Unfiltered, the seeded directory alone puts the count well above one.
    await list.search(name);
    await expect(list.row(name)).toBeVisible();
    await expect(page.getByTestId('ehr-row')).toHaveCount(1);
  });
});
