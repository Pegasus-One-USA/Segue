export const environment = {
  production: true,
  // Empty on purpose: this app's own backend now serves this build directly (same origin) —
  // see Demo_TestApp/backend/Program.cs's UseStaticFiles()/MapFallbackToFile().
  healthAppBase: '',
  // Substituted by the CI workflow before building (see .github/workflows/deploy.yml's "Configure
  // Demo app's FHIRBridge origin" step) with the production FHIRBridge.Gateway's real public
  // origin — this app calls the real FHIRBridge API directly, cross-origin from wherever this Demo
  // app is hosted, so it needs an absolute URL. Building this configuration by hand without
  // substituting the token first ships an obviously-broken URL rather than a silently-wrong one.
  // Whichever origin ends up here must also be added to FHIRBridge's allowed CORS origins
  // (SuperAdmin > CORS Origins in the portal) for these calls to succeed.
  fhirbridgeBase: '__FHIRBRIDGE_GATEWAY_ORIGIN__',
};
