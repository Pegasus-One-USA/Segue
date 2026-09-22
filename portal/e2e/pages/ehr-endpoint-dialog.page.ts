import { Page, Locator, expect } from '@playwright/test';

export interface EndpointFormValues {
  vendor?: string;
  endpointType?: string;
  vendorEndpointId?: string;
  name?: string;
  fhirBaseUrl?: string;
  formatType?: string;
  status?: string;
}

/**
 * The Add / Edit EHR Endpoint dialog.
 *
 * Its form fields already carried stable ids (ehr-vendor, ehr-name, ...) before these tests
 * existed, so they are used directly rather than adding duplicate test ids.
 */
export class EhrEndpointDialog {
  constructor(private readonly page: Page) {}

  get saveButton(): Locator {
    return this.page.getByTestId('ehr-dialog-save');
  }

  get error(): Locator {
    return this.page.getByTestId('ehr-dialog-error');
  }

  /** Every field currently flagged invalid by the form. */
  get invalidFields(): Locator {
    return this.page.locator('.dialog-form [data-invalid="true"]');
  }

  /** The invalid-state marker for one field, e.g. fieldError('fhirBaseUrl'). */
  fieldError(control: string): Locator {
    return this.page.locator(`.dialog-form [data-invalid="true"]:has(#ehr-${control})`);
  }

  /** Dismisses the dialog without saving. */
  async close(): Promise<void> {
    await this.page.keyboard.press('Escape');
    await expect(this.saveButton).toBeHidden();
  }

  async expectOpen(mode: 'Add' | 'Edit'): Promise<void> {
    await expect(this.page.getByRole('heading', { name: `${mode} EHR Endpoint` })).toBeVisible();
  }

  async fill(values: EndpointFormValues): Promise<void> {
    if (values.vendor !== undefined) await this.page.locator('#ehr-vendor').selectOption(values.vendor);
    if (values.endpointType !== undefined) await this.page.locator('#ehr-endpointType').selectOption(values.endpointType);
    if (values.vendorEndpointId !== undefined) await this.page.locator('#ehr-vendorEndpointId').fill(values.vendorEndpointId);
    if (values.name !== undefined) await this.page.locator('#ehr-name').fill(values.name);
    if (values.fhirBaseUrl !== undefined) await this.page.locator('#ehr-fhirBaseUrl').fill(values.fhirBaseUrl);
    if (values.formatType !== undefined) await this.page.locator('#ehr-formatType').fill(values.formatType);
    if (values.status !== undefined) await this.page.locator('#ehr-status').fill(values.status);
  }

  /** Saves and waits for the dialog to close -- i.e. the write actually succeeded. */
  async save(): Promise<void> {
    await this.saveButton.click();
    await expect(this.saveButton).toBeHidden({ timeout: 20_000 });
  }

  /** Saves expecting the write to be rejected; the dialog stays open. */
  async saveExpectingFailure(): Promise<void> {
    await this.saveButton.click();
    await expect(this.saveButton).toBeVisible();
  }

  async readValues(): Promise<Required<EndpointFormValues>> {
    return {
      vendor: await this.page.locator('#ehr-vendor').inputValue(),
      endpointType: await this.page.locator('#ehr-endpointType').inputValue(),
      vendorEndpointId: await this.page.locator('#ehr-vendorEndpointId').inputValue(),
      name: await this.page.locator('#ehr-name').inputValue(),
      fhirBaseUrl: await this.page.locator('#ehr-fhirBaseUrl').inputValue(),
      formatType: await this.page.locator('#ehr-formatType').inputValue(),
      status: await this.page.locator('#ehr-status').inputValue(),
    };
  }
}
