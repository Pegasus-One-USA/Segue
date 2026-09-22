# FHIRBridge portal — end-to-end tests

Playwright specs that drive the real portal in a real browser against the running local stack.
Nobody clicks anything: `npm test` opens Chromium, logs in, exercises a screen and asserts the
result.

## Prerequisites

1. **The stack must already be running** — API on :5000, portal on :4200:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .claude/skills/runsegue/scripts/runsegue.ps1
   ```
2. **Credentials** in `.env.e2e` at the **repo root** (gitignored — never commit it):
   ```
   E2E_ADMIN_EMAIL=...
   E2E_ADMIN_PASSWORD=...
   E2E_BASE_URL=http://localhost:4200
   E2E_API_URL=http://localhost:5000
   ```
   Every value is required; a missing one fails the run immediately rather than defaulting.

## Running

```bash
cd portal/e2e
npm install
npm run install:browsers   # once

npm test                   # headless
npm run test:headed        # watch it drive the browser
npm run test:ui            # interactive runner
npm run report             # open the last HTML report
```

## What these tests run against

They use the **shared `FHIRBridge_v2` dev database** — the same one you develop against, not a
throwaway. That is a deliberate choice, and it constrains how the specs are written:

- **Every row they create is prefixed `e2e-test`.** `afterAll` deletes every row carrying that
  prefix, via the API, so it still cleans up after a spec that failed halfway.
- **Nothing seeded is ever touched.** The table holds ~480 rows (the Epic sandbox plus Epic's
  imported MyChart directory). Cleanup only ever matches the prefix.
- **Deletes are soft.** `ISoftDeletable` means removed rows stay physically present with
  `IsDeleted = true`. They are invisible to the app; to purge them physically, delete where
  `Name LIKE 'e2e-test%'` directly.
- **Specs run serially** (`workers: 1`). Parallel workers creating and deleting rows in one
  shared list produce flaky counts.

## Things worth knowing before writing a new spec

**EHR Endpoints is not a route.** It is a launcher row on System Settings > General that opens
the list as a full-screen dialog. `/ehr-endpoints` redirects to General without opening anything.
See `pages/ehr-endpoints.page.ts`.

**The list is paginated and never empty.** With ~480 seeded rows, a row you just created is not
on screen. Search for it by name; do not assert on the first page.

**Search is debounced and de-duplicated.** The component pipes input through `debounceTime(300)`
and `distinctUntilChanged`. Re-filling the box with the term it already holds emits nothing, so
there is no request to wait on — and clearing then retyping inside the debounce window collapses
into a single emission that may be swallowed, leaving the box showing a term while the list shows
everything. `EhrEndpointsPage.search()` handles this; use it rather than typing into the box.

**Auth is HttpOnly cookies, not bearer tokens.** An `Authorization: Bearer` header gets a 401.
API calls must come from a context built on the saved `storageState`.

**State-changing API calls need a CSRF header.** The API double-submits: `X-CSRF-Token` must equal
the `fhirbridge_csrf` cookie or the request is 403'd. GETs are exempt, which is why listing works
without it but DELETE does not. See `fixtures/cleanup.ts`.

## Secrets and PHI

- `.env.e2e` is gitignored and must never be committed. No credential belongs in a spec file.
- Traces, screenshots and video are captured **on failure only** and never on success. A trace is
  a full DOM snapshot: a trace of a failed login contains whatever was typed into the password
  field, and a trace of a run against real data would contain that data.
- These specs are for **synthetic/sandbox data only**. Do not point them at a production EHR
  tenant or any source holding real patient data. The PHI-masking log enricher protects the
  application's own output; it does not cover Playwright's artifacts.

## Layout

```
fixtures/env.ts        config from .env.e2e; required-with-no-fallback; e2e naming helpers
fixtures/cleanup.ts    API teardown of e2e rows (cookie auth + CSRF header)
pages/                 page objects — where DOM knowledge lives
tests/auth.setup.ts    logs in once, saves the session other specs reuse
tests/*.spec.ts        the specs
```

Selectors use `data-testid` so ordinary UI changes do not break the suite. Add one when you need
a new hook rather than selecting on CSS classes or Material's generated markup.
