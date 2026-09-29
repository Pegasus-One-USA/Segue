import { test, expect } from '@playwright/test';
import { EhrEndpointsPage } from '../pages/ehr-endpoints.page';
import { EhrEndpointDialog } from '../pages/ehr-endpoint-dialog.page';
import { uniqueName, env } from '../fixtures/env';
import { deleteE2eEndpoints } from '../fixtures/cleanup';
import { watchWrites } from '../fixtures/no-write';

/**
 * Negative paths for the Add EHR Endpoint dialog.
 *
 * The happy-path spec proves a valid endpoint saves. These prove an INVALID one does not: the
 * dialog stays open, the offending fields are marked, and -- critically -- no write reaches the
 * API. A form that silently posts junk is the failure mode worth guarding against.
 *
 * Contract under test (ehr-endpoint-dialog.component.ts save()): on an invalid form it sets
 * submitted, marks every control touched and returns BEFORE calling the service.
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

test.describe('EHR Endpoints — validation', () => {
  test('saving an empty form is rejected and posts nothing', async ({ page }) => {
    const list = new EhrEndpointsPage(page);
    const dialog = new EhrEndpointDialog(page);

    const writes = watchWrites(page);

    await list.open();
    await list.addButton.click();
    await dialog.expectOpen('Add');

    // Clear the three text fields that ship with defaults, so the form is genuinely empty.
    await dialog.fill({ vendorEndpointId: '', name: '', fhirBaseUrl: '', formatType: '', status: '' });
    await dialog.saveExpectingFailure();

    await expect(dialog.invalidFields).toHaveCount(5);
    writes.assertNone();
  });

  test('a single missing required field blocks the save', async ({ page }) => {
    const list = new EhrEndpointsPage(page);
    const dialog = new EhrEndpointDialog(page);
    const name = uniqueName('validation-missing-url');
    const writes = watchWrites(page);

    await list.open();
    await list.addButton.click();
    await dialog.expectOpen('Add');

    // Everything valid except FHIR Base URL.
    await dialog.fill({
      vendor: 'GenericFhir',
      endpointType: 'MyChart',
      vendorEndpointId: `${name}-id`,
      name,
      fhirBaseUrl: '',
      formatType: 'R4',
      status: 'active',
    });
    await dialog.saveExpectingFailure();

    await expect(dialog.fieldError('fhirBaseUrl')).toBeVisible();
    await expect(dialog.invalidFields).toHaveCount(1);
    writes.assertNone();

    // And nothing was persisted under that name.
    await dialog.close();
    await list.search(name);
    await expect(list.row(name)).toHaveCount(0);
  });

  test('over-length input is rejected rather than silently truncated', async ({ page }) => {
    const list = new EhrEndpointsPage(page);
    const dialog = new EhrEndpointDialog(page);
    const name = uniqueName('validation-maxlength');
    const writes = watchWrites(page);

    await list.open();
    await list.addButton.click();
    await dialog.expectOpen('Add');

    await dialog.fill({
      vendor: 'GenericFhir',
      endpointType: 'MyChart',
      // maxLength(100) on vendorEndpointId.
      vendorEndpointId: 'x'.repeat(101),
      name,
      fhirBaseUrl: 'https://e2e.example.org/api/FHIR/R4/',
      formatType: 'R4',
      status: 'active',
    });
    await dialog.saveExpectingFailure();

    await expect(dialog.fieldError('vendorEndpointId')).toBeVisible();
    writes.assertNone();

    await dialog.close();
    await list.search(name);
    await expect(list.row(name)).toHaveCount(0);
  });

  test('correcting the error lets the save through', async ({ page }) => {
    const list = new EhrEndpointsPage(page);
    const dialog = new EhrEndpointDialog(page);
    const name = uniqueName('validation-recover');

    await list.open();
    await list.addButton.click();
    await dialog.expectOpen('Add');

    await dialog.fill({
      vendor: 'GenericFhir',
      endpointType: 'MyChart',
      vendorEndpointId: `${name}-id`,
      name,
      fhirBaseUrl: '',
      formatType: 'R4',
      status: 'active',
    });
    await dialog.saveExpectingFailure();
    await expect(dialog.fieldError('fhirBaseUrl')).toBeVisible();

    // Supply the missing value; the same Save must now succeed.
    await dialog.fill({ fhirBaseUrl: 'https://e2e.example.org/api/FHIR/R4/' });
    await dialog.save();

    await list.search(name);
    await expect(list.row(name)).toBeVisible();
  });
});
