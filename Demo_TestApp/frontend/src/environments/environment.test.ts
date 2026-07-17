export const environment = {
  production: true,
  // Empty on purpose: this app's own backend serves this build directly (same origin) — see
  // Demo_TestApp/backend/Program.cs's UseStaticFiles()/MapFallbackToFile().
  healthAppBase: '',
  // Substituted by the CI workflow before building (see .github/workflows/deploy.yml's "Configure
  // Demo app's FHIRBridge origin" step) with the test FHIRBridge.Gateway's real origin — a
  // different port on the same VM as production (see deploy/windows/README.md). Building this
  // configuration by hand without substituting the token first ships an obviously-broken URL
  // rather than a silently-wrong one.
  fhirbridgeBase: '__FHIRBRIDGE_GATEWAY_ORIGIN__',
};
