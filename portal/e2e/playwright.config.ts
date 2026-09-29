import { defineConfig, devices } from '@playwright/test';
import { config as loadEnv } from 'dotenv';
import { resolve } from 'node:path';

// Credentials and target URLs live in .env.e2e at the repo root, which is gitignored.
// Nothing sensitive is ever committed here — see README.md.
loadEnv({ path: resolve(__dirname, '../../.env.e2e') });

const baseURL = process.env.E2E_BASE_URL ?? 'http://localhost:4200';

export default defineConfig({
  testDir: './tests',

  // These specs write to the shared FHIRBridge_v2 dev database, so they must not run
  // concurrently with each other -- two workers creating/deleting rows in the same list
  // produce flaky counts. Correctness over speed.
  fullyParallel: false,
  workers: 1,

  // A failing run should say so, not silently pass on a retry. One retry absorbs genuine
  // flakiness (a slow dialog animation) without hiding a real regression.
  retries: process.env.CI ? 1 : 0,
  forbidOnly: !!process.env.CI,

  timeout: 60_000,
  expect: { timeout: 10_000 },

  reporter: [['html', { open: 'never' }], ['list']],

  use: {
    baseURL,
    // PHI/secret hygiene: traces and screenshots snapshot the live DOM, and a trace of a failed
    // login contains whatever was typed into the password field. Capture on failure only, never
    // on success, so nothing is retained for a run that went fine.
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    actionTimeout: 15_000,
    navigationTimeout: 30_000,
  },

  projects: [
    // Logs in once and saves the session; every other project reuses it.
    { name: 'setup', testMatch: /auth\.setup\.ts/ },
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'], storageState: '.auth/user.json' },
      dependencies: ['setup'],
    },
  ],
});
