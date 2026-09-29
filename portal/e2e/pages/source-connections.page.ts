import { Page, Locator, expect } from '@playwright/test';

/** The Source Connections master list (Settings > Workflow Configurations > Source Connections). */
export class SourceConnectionsPage {
  constructor(private readonly page: Page) {}

  async open(): Promise<void> {
    await this.page.goto('/settings/workflow-configurations/source-connections');
    await expect(this.addButton).toBeVisible();
  }

  get addButton(): Locator {
    return this.page.getByTestId('src-add');
  }

  get searchInput(): Locator {
    return this.page.getByTestId('src-search');
  }

  /** "New Source Connection" opens a vendor card grid rather than jumping into a form. */
  async startCreate(vendor: 'Epic'): Promise<void> {
    await this.addButton.click();
    await this.page.locator(`[data-testid="src-vendor-card"][data-vendor="${vendor}"]`).click();
  }

  row(name: string): Locator {
    return this.page.locator(`[data-testid="src-row"][data-row-name="${name}"]`);
  }

  /**
   * Filters the list down to one connection by name.
   *
   * Same debounce/distinctUntilChanged shape as the EHR Endpoints search: re-typing a term the box
   * already holds emits nothing, so only type when the value actually differs.
   */
  async search(term: string): Promise<void> {
    const current = await this.searchInput.inputValue();
    if (current !== term) {
      await this.searchInput.fill(term);
      await this.page.waitForTimeout(600);
    }
  }

  async filterByAudience(value: string): Promise<void> {
    await this.page.getByTestId('src-filter-audience').selectOption(value);
    await this.page.waitForTimeout(600);
  }

  async rowAction(name: string, action: 'view' | 'edit' | 'delete'): Promise<void> {
    await this.row(name).getByTestId('src-row-menu').click();
    await this.page.getByTestId(`src-row-${action}`).click();
  }
}
