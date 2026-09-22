import { test, expect } from '@playwright/test';
import { EhrEndpointsPage } from '../pages/ehr-endpoints.page';
import { EhrEndpointDialog } from '../pages/ehr-endpoint-dialog.page';
import { uniqueName, env } from '../fixtures/env';
import { deleteE2eEndpoints } from '../fixtures/cleanup';

/**
 * Narrated walkthrough: the same CRUD flow the real spec covers, but capturing a screenshot at
 * each step so a human can see what the automation actually does.
 *
 * Kept separate from ehr-endpoints.spec.ts because screenshotting a PASSING run is a
 * demonstration aid, not a test concern -- the real suite captures on failure only.
 */

const shot = (name: string) => `screenshots/${name}.png`;

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

test('walkthrough: create, edit and delete an EHR endpoint', async ({ page }) => {
  const list = new EhrEndpointsPage(page);
  const dialog = new EhrEndpointDialog(page);
  const name = uniqueName('walkthrough');

  await page.goto('/settings/system-settings/general');
  await page.screenshot({ path: shot('01-system-settings'), fullPage: false });

  await list.open();
  await page.screenshot({ path: shot('02-ehr-endpoints-list') });

  await list.addButton.click();
  await dialog.expectOpen('Add');
  await page.screenshot({ path: shot('03-add-dialog-empty') });

  await dialog.fill({
    vendor: 'GenericFhir',
    endpointType: 'MyChart',
    vendorEndpointId: `${name}-id`,
    name,
    fhirBaseUrl: 'https://e2e.example.org/api/FHIR/R4/',
    formatType: 'R4',
    status: 'active',
  });
  await page.screenshot({ path: shot('04-add-dialog-filled') });

  await dialog.save();
  await list.search(name);
  await expect(list.row(name)).toBeVisible();
  await page.screenshot({ path: shot('05-created-and-found') });

  await list.rowAction(name, 'edit');
  await dialog.expectOpen('Edit');
  await dialog.fill({ fhirBaseUrl: 'https://e2e-updated.example.org/api/FHIR/R4/' });
  await page.screenshot({ path: shot('06-edit-dialog') });

  await dialog.save();
  await list.search(name);
  await page.screenshot({ path: shot('07-edit-persisted') });

  await list.rowAction(name, 'delete');
  await page.screenshot({ path: shot('08-delete-confirm') });

  await page.getByTestId('confirm-accept').click();
  await list.search(name);
  await expect(list.row(name)).toHaveCount(0);
  await page.screenshot({ path: shot('09-deleted-gone') });
});
