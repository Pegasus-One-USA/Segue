import { test as setup, expect } from '@playwright/test';
import { env } from '../fixtures/env';

const AUTH_STATE = '.auth/user.json';

/**
 * Logs in once and saves the browser session for every other spec to reuse.
 *
 * The session is HttpOnly cookies, not localStorage -- the API strips the raw tokens from the
 * response body and sets them as cookies instead (see AuthController.IssueTokenCookiesAndStrip),
 * so storageState has to capture cookies. Playwright does that by default.
 */
setup('authenticate', async ({ page }) => {
  await page.goto('/auth/login');

  await page.getByTestId('login-email').fill(env.adminEmail);
  await page.getByTestId('login-password').fill(env.adminPassword);
  await page.getByTestId('login-submit').click();

  // Landing anywhere inside the app shell means the session is live. Waiting on the URL rather
  // than a specific dashboard widget keeps this from breaking when the dashboard changes.
  await page.waitForURL(url => !url.pathname.startsWith('/auth'), { timeout: 30_000 });
  await expect(page).not.toHaveURL(/\/auth\/login/);

  await page.context().storageState({ path: AUTH_STATE });
});
