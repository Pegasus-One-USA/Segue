import { APP_ORIGIN, absoluteJwksUrl, hostedJwksUrl, isHostedJwksUrl } from './api-endpoints';

/**
 * FHIRBridge's hosted JWKS URL is what the EHR fetches from outside, so it is always absolute. Settings forms used to
 * build it from environment.apiBase, which is empty in production, and saved a bare path the JWKS URL field then
 * refused ("Enter a valid JWKS URL").
 */
describe('hosted JWKS URL', () => {
  const id = '9d328e68-a9c4-4da9-873e-d6092d71f25b';
  const path = `/api/v1/source-connections/${id}/.well-known/jwks.json`;

  it('is built absolute, on the API origin', () => {
    expect(APP_ORIGIN).toMatch(/^https?:\/\//);
    expect(hostedJwksUrl(id)).toBe(`${APP_ORIGIN}${path}`);
  });

  it('upgrades a hosted path saved without its origin', () => {
    expect(absoluteJwksUrl(path)).toBe(`${APP_ORIGIN}${path}`);
    expect(absoluteJwksUrl(`  ${path} `)).toBe(`${APP_ORIGIN}${path}`);
  });

  it('leaves an absolute or external URL, and an empty value, as they are', () => {
    expect(absoluteJwksUrl(`${APP_ORIGIN}${path}`)).toBe(`${APP_ORIGIN}${path}`);
    expect(absoluteJwksUrl('https://app.example.com/.well-known/jwks.json')).toBe('https://app.example.com/.well-known/jwks.json');
    expect(absoluteJwksUrl('/some/other/path.json')).toBe('/some/other/path.json');
    expect(absoluteJwksUrl(null)).toBe('');
    expect(absoluteJwksUrl(undefined)).toBe('');
  });

  it('recognises the hosted URL with or without its origin, and nothing else', () => {
    expect(isHostedJwksUrl(path)).toBeTrue();
    expect(isHostedJwksUrl(`${APP_ORIGIN}${path}`)).toBeTrue();
    expect(isHostedJwksUrl('https://app.example.com/.well-known/jwks.json')).toBeFalse();
    expect(isHostedJwksUrl(`https://elsewhere.example.com${path}`)).toBeFalse();
    expect(isHostedJwksUrl('')).toBeFalse();
  });
});
