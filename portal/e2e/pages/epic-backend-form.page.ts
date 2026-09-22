import { Page, Locator, expect } from '@playwright/test';
import { env } from '../fixtures/env';

/**
 * The Epic source-connection wizard, driven as the Backend System audience.
 *
 * This is the v1 form (components/shared/ehr-vendor-source-form), which is what the Source
 * Connections master screen imports. The workflow builder uses a separate v2 copy.
 *
 * The wizard's own fields already carry stable ids (eaf-*), so those are used directly; only the
 * buttons and result panels needed test ids.
 *
 * These drive the form's REAL outbound calls to Epic -- Discover hits
 * /.well-known/smart-configuration, and granted scopes performs an actual client_credentials +
 * private_key_jwt exchange. Timeouts here are sized for a live vendor round-trip, not a stub.
 */
export class EpicBackendForm {
  constructor(private readonly page: Page) {}

  // ── Fields ────────────────────────────────────────────────────────────────
  get appName(): Locator { return this.page.locator('#eaf-appName'); }
  get audience(): Locator { return this.page.locator('#eaf-audience'); }
  get environment(): Locator { return this.page.locator('#eaf-environment'); }
  get baseUrl(): Locator { return this.page.locator('#eaf-epicBaseUrl'); }
  get tokenEndpoint(): Locator { return this.page.locator('#eaf-tokenEndpoint'); }
  get authzEndpoint(): Locator { return this.page.locator('#eaf-authzEndpoint'); }
  get clientId(): Locator { return this.page.locator('#eaf-clientId'); }
  get authMethod(): Locator { return this.page.locator('#eaf-authMethod'); }
  get jwksUrl(): Locator { return this.page.locator('#eaf-jwksUrl'); }
  get keySource(): Locator { return this.page.locator('#eaf-keySource'); }
  get jwtKid(): Locator { return this.page.locator('#eaf-jwtKid'); }
  get privateKeyRef(): Locator { return this.page.locator('#eaf-privateKeyRef'); }
  get privateKeySecretName(): Locator { return this.page.locator('#eaf-privateKeySecretName'); }

  // ── Buttons / panels ──────────────────────────────────────────────────────
  get discoverButton(): Locator { return this.page.getByTestId('src-discover'); }
  get saveButton(): Locator { return this.page.getByTestId('src-save'); }
  get cancelButton(): Locator { return this.page.getByTestId('src-cancel'); }
  get missingHint(): Locator { return this.page.getByTestId('src-missing-hint'); }
  get grantedScopes(): Locator { return this.page.getByTestId('src-granted-scopes'); }
  get grantedScopesError(): Locator { return this.page.getByTestId('src-granted-scopes-error'); }
  get keyFileInput(): Locator { return this.page.getByTestId('src-key-file'); }
  get keyImportButton(): Locator { return this.page.getByTestId('src-key-import'); }

  /**
   * The Key ID as shown when REOPENING a connection that already has a signing key.
   *
   * On reopen the wizard renders the key read-only (a summary list, not inputs) so an unrelated
   * edit cannot silently replace a key already registered with the vendor -- so #eaf-jwtKid does
   * not exist in that state and must be read from here instead.
   */
  get existingKeyId(): Locator { return this.page.getByTestId('src-existing-kid'); }

  async expectOpen(): Promise<void> {
    await expect(this.audience).toBeVisible();
  }

  async selectBackendSystem(): Promise<void> {
    await this.audience.selectOption('backend-system');
  }

  /** Clicks Discover and waits for Epic's real smart-configuration response to land in the form. */
  async discover(): Promise<void> {
    const probe = this.page.waitForResponse(
      r => r.url().includes('/source-discovery/probe') && r.request().method() === 'POST',
      { timeout: 60_000 }
    );
    await this.discoverButton.click();
    const res = await probe;
    expect(res.ok(), 'live discovery against Epic should succeed').toBeTruthy();
    // The form fills from the response; wait for the value to actually appear.
    await expect(this.tokenEndpoint).not.toHaveValue('', { timeout: 20_000 });
  }

  /**
   * Uploads the real PEM and clicks Import, which provisions the key server-side and fills in the
   * Key ID / Key Vault Name / Secret Name fields.
   */
  async importPrivateKey(): Promise<void> {
    await this.keySource.selectOption('import');
    await this.keyFileInput.setInputFiles(env.epicPrivateKeyPath);

    const imported = this.page.waitForResponse(
      r => r.url().includes('/import-signing-key') && r.request().method() === 'POST',
      { timeout: 60_000 }
    );
    await this.keyImportButton.click();
    const res = await imported;
    expect(res.ok(), 'importing the Epic signing key should succeed').toBeTruthy();

    await expect(this.jwtKid).not.toHaveValue('', { timeout: 20_000 });
  }

  /**
   * Overrides the kid and JWKS URL that Import just generated with the pair Epic actually has
   * registered.
   *
   * Import always mints a fresh kid and points the JWKS URL at this instance. Epic knows neither,
   * so without this step the saved connection cannot authenticate -- the signed assertion carries
   * a kid the vendor has never seen. Doing it by hand is the documented flow for a key that is
   * already registered (see the wizard's own hint on the JWKS URL field).
   */
  async applyRegisteredKeyIdentity(): Promise<void> {
    await this.jwtKid.fill(env.epicKeyId);
    await this.jwksUrl.fill(env.epicJwksUrl);
  }

  /**
   * The full on-screen flow for an Epic Backend System source:
   * import the existing key -> override kid/JWKS URL -> client id -> FHIR base URL -> Discover.
   */
  async fillBackendSystem(name: string): Promise<void> {
    await this.appName.fill(name);
    await this.selectBackendSystem();
    await this.environment.selectOption('sandbox');
    await this.importPrivateKey();
    await this.applyRegisteredKeyIdentity();
    await this.clientId.fill(env.epicClientId);
    await this.baseUrl.fill(env.epicFhirBaseUrl);
    await this.discover();
    // The name must still be what we typed by the time Save runs.
    await expect(this.appName).toHaveValue(name);
  }

  async save(): Promise<void> {
    await this.saveButton.click();
    await expect(this.saveButton).toBeHidden({ timeout: 30_000 });
  }

  /** Clicks Save expecting it to be refused; the form stays open. */
  async saveExpectingRefusal(): Promise<void> {
    await this.saveButton.click();
    await expect(this.saveButton).toBeVisible();
  }

  /** Ids of every eaf-* field currently rendered and visible. */
  async visibleFieldIds(): Promise<string[]> {
    return this.page.evaluate(() =>
      Array.from(document.querySelectorAll('[id^="eaf-"]'))
        .filter(el => (el as HTMLElement).offsetParent !== null)
        .map(el => el.id)
    );
  }
}
