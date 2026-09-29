import { Page, expect } from '@playwright/test';

/**
 * Records every write the page attempts to the EHR endpoints API.
 *
 * A validation test that only checks for a red field is weak: removing the form.invalid gate in
 * the dialog's save() leaves those markers in place (they come from markAllAsTouched and the
 * submitted signal) while the request goes out anyway. Asserting that NO write left the browser
 * is what actually pins the behaviour.
 */
export function watchWrites(page: Page): { assertNone(): void } {
  const writes: string[] = [];

  page.on('request', r => {
    const method = r.method();
    const isWrite = method === 'POST' || method === 'PUT' || method === 'PATCH';
    if (isWrite && r.url().includes('/ehr-endpoints')) {
      writes.push(`${method} ${r.url()}`);
    }
  });

  return {
    assertNone() {
      expect(writes, 'an invalid form must not write to the API').toEqual([]);
    },
  };
}
