import { Page, Locator, expect } from '@playwright/test';

/**
 * The EHR Endpoints master screen.
 *
 * It is NOT a route: it is a launcher row on System Settings > General that opens the list as a
 * full-screen dialog. The old /ehr-endpoints URL just redirects to General without opening
 * anything, so navigation has to go through the launcher row.
 */
export class EhrEndpointsPage {
  constructor(private readonly page: Page) {}

  /** Opens the screen via its launcher row on System Settings > General. */
  async open(): Promise<void> {
    await this.page.goto('/settings/system-settings/general');

    const row = this.page.locator('[data-testid="settings-launcher-row"][data-launcher-code="ehr-endpoints"]');
    await expect(row).toBeVisible();

    await row.getByTestId('settings-row-menu').click();
    await this.page.getByTestId('settings-launcher-open').click();

    await expect(this.page.getByRole('heading', { name: 'EHR Endpoints' })).toBeVisible();
    await expect(this.addButton).toBeVisible();
  }

  get addButton(): Locator {
    return this.page.getByTestId('ehr-add');
  }

  get searchInput(): Locator {
    return this.page.getByTestId('ehr-search');
  }

  get entriesInfo(): Locator {
    return this.page.getByTestId('ehr-entries-info');
  }

  /**
   * Finds a row by endpoint name.
   *
   * The list holds ~480 seeded rows and is paginated, so a newly created row is almost never on
   * the visible page. Callers search first, then locate -- never assume a row is on screen.
   */
  row(name: string): Locator {
    return this.page.locator(`[data-testid="ehr-row"][data-row-name="${name}"]`);
  }

  /**
   * Types into the search box and waits for the filtered list to settle.
   *
   * The component debounces input by 300ms and pipes it through distinctUntilChanged (see
   * ehr-endpoint-list.component.ts). Two consequences:
   *
   *   - Re-filling the box with the term it already holds emits nothing, so there is no request
   *     to wait for. The list is already correct; just return.
   *   - Clearing and retyping within the debounce window collapses to a single emission that
   *     distinctUntilChanged may then swallow, leaving the box showing the term while the list
   *     shows everything. So never clear as a way to force a reload.
   */
  async search(term: string): Promise<void> {
    const current = await this.searchInput.inputValue();

    if (current !== term) {
      await this.searchInput.fill(term);
      // Outlast the 300ms debounce before asserting on the result.
      await this.page.waitForTimeout(600);
    }

    // The unfiltered list holds ~480 seeded rows; a settled total inside one page proves the
    // filter was actually applied rather than the full list still being on screen.
    await expect
      .poll(async () => (await this.entriesInfo.textContent())?.trim() ?? '', {
        timeout: 20_000,
        message: `list never settled on results for "${term}"`,
      })
      .toMatch(/No entries found|Showing 1 to \d+ of \d{1,2} entr/);
  }

  async filterBySource(vendor: string): Promise<void> {
    await this.page.getByTestId('ehr-filter-source').selectOption(vendor);
  }

  async filterByStatus(active: 'true' | 'false' | ''): Promise<void> {
    await this.page.getByTestId('ehr-filter-status').selectOption(active);
  }

  async resetFilters(): Promise<void> {
    await this.page.getByTestId('ehr-reset').click();
  }

  /** Opens the row kebab menu and picks an action. */
  async rowAction(name: string, action: 'edit' | 'delete'): Promise<void> {
    await this.row(name).getByTestId('ehr-row-menu').click();
    await this.page.getByTestId(`ehr-row-${action}`).click();
  }
}
