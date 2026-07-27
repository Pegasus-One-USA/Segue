export const environment = {
  production: true,
  // Empty on purpose: this app's own backend now serves this build directly (same origin) —
  // see Demo_TestApp/backend/Program.cs's UseStaticFiles()/MapFallbackToFile().
  healthAppBase: '',
  // The deployed FHIRBridge.Gateway's real public origin — this app calls the real FHIRBridge API
  // directly, cross-origin from wherever this Demo app is hosted, so it needs an absolute URL. Same
  // value already configured as StandaloneBaseUrl/PatientBaseUrl in this deployment's Workflow
  // Settings (WorkflowSettingsEntity) — kept in sync here since Provider_InApp's launch redirect
  // (launch-provider-in-app.ts) has no runtime override for this value, unlike Patient/Provider
  // Standalone. The FHIRBridge API's Portal:AllowedOrigins must also include this Demo app's own
  // origin for these calls to succeed (CORS).
  fhirbridgeBase: 'https://segue.pegasusone.com:60031',
};
