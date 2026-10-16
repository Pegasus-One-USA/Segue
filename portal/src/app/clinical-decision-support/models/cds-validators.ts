import { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';

/** Service id rule, same as the API: lower-case letters and digits in groups joined by single hyphens. */
export const CDS_SERVICE_ID_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;

/** Turns a title into a service id suggestion, e.g. "Renal dose check (ICU)" → "renal-dose-check-icu". */
export function toCdsServiceId(text: string): string {
  return text
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 100)
    .replace(/-+$/g, '');
}

/** Blank passes; anything else must be an absolute http(s) URL. Error key: `url`. */
export const absoluteHttpUrlValidator: ValidatorFn = (control: AbstractControl): ValidationErrors | null => {
  const value = typeof control.value === 'string' ? control.value.trim() : '';
  if (!value) return null;
  try {
    const url = new URL(value);
    return url.protocol === 'http:' || url.protocol === 'https:' ? null : { url: true };
  } catch {
    return { url: true };
  }
};

/** Blank passes; anything else must be an absolute https URL (signing keys are never fetched in clear text). Error key: `httpsUrl`. */
export const absoluteHttpsUrlValidator: ValidatorFn = (control: AbstractControl): ValidationErrors | null => {
  const value = typeof control.value === 'string' ? control.value.trim() : '';
  if (!value) return null;
  try {
    return new URL(value).protocol === 'https:' ? null : { httpsUrl: true };
  } catch {
    return { httpsUrl: true };
  }
};

/**
 * Blank passes; anything else must be an absolute http(s) URL with no query or fragment (an EHR FHIR base). http is
 * accepted for a dev FHIR server; the API calls it only when insecure callbacks are allowed. Error key: `fhirBaseUrl`.
 */
export const fhirBaseUrlValidator: ValidatorFn = (control: AbstractControl): ValidationErrors | null => {
  const value = typeof control.value === 'string' ? control.value.trim() : '';
  if (!value) return null;
  try {
    const url = new URL(value);
    const httpish = url.protocol === 'http:' || url.protocol === 'https:';
    return httpish && !value.includes('?') && !value.includes('#') ? null : { fhirBaseUrl: true };
  } catch {
    return { fhirBaseUrl: true };
  }
};

/** Blank passes; anything else must be a JSON object with a non-empty "keys" array (a JWKS). Error key: `jwks`. */
export const jwksJsonValidator: ValidatorFn = (control: AbstractControl): ValidationErrors | null => {
  const value = typeof control.value === 'string' ? control.value.trim() : '';
  if (!value) return null;
  try {
    const parsed: unknown = JSON.parse(value);
    const keys = (parsed as { keys?: unknown } | null)?.keys;
    const ok = !!parsed && typeof parsed === 'object' && !Array.isArray(parsed) && Array.isArray(keys) && keys.length > 0;
    return ok ? null : { jwks: true };
  } catch {
    return { jwks: true };
  }
};
