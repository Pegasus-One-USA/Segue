import { buildCdsSettingsGroup, parseCdsList, readCdsSettings } from './cds-settings.util';
import { absoluteHttpUrlValidator, absoluteHttpsUrlValidator, fhirBaseUrlValidator, jwksJsonValidator, toCdsServiceId } from './cds-validators';
import { FormControl } from '@angular/forms';
import { makeSetting } from '../testing/cds-test-data';

describe('cds-settings.util', () => {
  const NUMBER = makeSetting({ key: 'egfr', type: 'number', defaultValue: 30, min: 5, max: 120 });
  const LIST = makeSetting({ key: 'codes', type: 'list', defaultValue: ['A1', 'B2'] });
  const BOOL = makeSetting({ key: 'strict', type: 'boolean', defaultValue: true });
  const SELECT = makeSetting({ key: 'level', type: 'select', defaultValue: 'warning', options: [{ value: 'warning', label: 'Warning' }] });
  const TEXT = makeSetting({ key: 'note', type: 'text', defaultValue: null, required: true });

  it('seeds each control from the stored value, falling back to the default', () => {
    const group = buildCdsSettingsGroup([NUMBER, LIST, BOOL, SELECT, TEXT], { egfr: 45 });

    expect(group.controls['egfr'].value).toBe(45);
    expect(group.controls['codes'].value).toBe('A1\nB2');
    expect(group.controls['strict'].value).toBeTrue();
    expect(group.controls['level'].value).toBe('warning');
    expect(group.controls['note'].value).toBe('');
  });

  it('reads the form back into API values', () => {
    const group = buildCdsSettingsGroup([NUMBER, LIST, BOOL, TEXT], null);
    group.controls['egfr'].setValue('');
    group.controls['codes'].setValue(' A1 \n\nB2, A1 ,C3');
    group.controls['strict'].setValue(false);
    group.controls['note'].setValue('  hi  ');

    expect(readCdsSettings([NUMBER, LIST, BOOL, TEXT], group)).toEqual({
      egfr: null, codes: ['A1', 'B2', 'C3'], strict: false, note: 'hi',
    });
  });

  it('sends a value left at its default as null, so the service keeps following the default', () => {
    const group = buildCdsSettingsGroup([NUMBER, LIST, BOOL, SELECT], { egfr: 45 });

    expect(readCdsSettings([NUMBER, LIST, BOOL, SELECT], group)).toEqual({
      egfr: 45, codes: null, strict: null, level: null,
    });
  });

  it('applies min, max and required only where the descriptor asks', () => {
    const group = buildCdsSettingsGroup([NUMBER, TEXT, makeSetting({ key: 'opt', type: 'text', required: true, defaultValue: 'x' })], null);

    group.controls['egfr'].setValue(1);
    expect(group.controls['egfr'].hasError('min')).toBeTrue();
    group.controls['egfr'].setValue(500);
    expect(group.controls['egfr'].hasError('max')).toBeTrue();
    expect(group.controls['note'].hasError('required')).toBeTrue();
    // Required with a default may be left blank: the API fills it in.
    group.controls['opt'].setValue('');
    expect(group.controls['opt'].valid).toBeTrue();
  });

  it('parses lists on new lines and commas, dropping blanks and repeats', () => {
    expect(parseCdsList('a\n b ,a,,c')).toEqual(['a', 'b', 'c']);
    expect(parseCdsList('')).toEqual([]);
  });

  it('suggests a service id from a name', () => {
    expect(toCdsServiceId('Renal dose check (ICU)')).toBe('renal-dose-check-icu');
    expect(toCdsServiceId('  --  ')).toBe('');
  });

  it('accepts only blank or absolute http(s) URLs', () => {
    expect(absoluteHttpUrlValidator(new FormControl(''))).toBeNull();
    expect(absoluteHttpUrlValidator(new FormControl('https://x.org/a'))).toBeNull();
    expect(absoluteHttpUrlValidator(new FormControl('javascript:alert(1)'))).toEqual({ url: true });
    expect(absoluteHttpUrlValidator(new FormControl('not a url'))).toEqual({ url: true });
  });

  it('accepts only blank or https URLs for a JWKS URL', () => {
    expect(absoluteHttpsUrlValidator(new FormControl(''))).toBeNull();
    expect(absoluteHttpsUrlValidator(new FormControl('https://ehr.example.org/jwks.json'))).toBeNull();
    expect(absoluteHttpsUrlValidator(new FormControl('http://ehr.example.org/jwks.json'))).toEqual({ httpsUrl: true });
    expect(absoluteHttpsUrlValidator(new FormControl('not a url'))).toEqual({ httpsUrl: true });
  });

  it('accepts blank or an http(s) FHIR base without query or fragment', () => {
    expect(fhirBaseUrlValidator(new FormControl(''))).toBeNull();
    expect(fhirBaseUrlValidator(new FormControl('https://ehr.example.org/api/FHIR/R4'))).toBeNull();
    expect(fhirBaseUrlValidator(new FormControl('http://hapi:8080/fhir'))).toBeNull();
    expect(fhirBaseUrlValidator(new FormControl('https://ehr.example.org/fhir?x=1'))).toEqual({ fhirBaseUrl: true });
    expect(fhirBaseUrlValidator(new FormControl('https://ehr.example.org/fhir#a'))).toEqual({ fhirBaseUrl: true });
    expect(fhirBaseUrlValidator(new FormControl('ftp://ehr.example.org'))).toEqual({ fhirBaseUrl: true });
  });

  it('accepts only blank or a JWKS with at least one key', () => {
    expect(jwksJsonValidator(new FormControl(''))).toBeNull();
    expect(jwksJsonValidator(new FormControl('{"keys":[{"kty":"RSA"}]}'))).toBeNull();
    expect(jwksJsonValidator(new FormControl('{"keys":[]}'))).toEqual({ jwks: true });
    expect(jwksJsonValidator(new FormControl('[1]'))).toEqual({ jwks: true });
    expect(jwksJsonValidator(new FormControl('{bad'))).toEqual({ jwks: true });
  });
});
