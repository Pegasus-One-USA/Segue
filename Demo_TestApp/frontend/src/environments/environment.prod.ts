export const environment = {
  production: true,
  // Empty on purpose: this app's own backend now serves this build directly (same origin) —
  // see Demo_TestApp/backend/Program.cs's UseStaticFiles()/MapFallbackToFile().
  healthAppBase: '',
  // The deployed FHIRBridge.Gateway's real public origin — this app calls the real FHIRBridge API
  // directly, cross-origin from wherever this Demo app is hosted, so it needs an absolute URL. Same
  // value already configured as StandaloneBaseUrl/PatientBaseUrl in this deployment's Workflow Settings
  // (WorkflowSettingsEntity) — kept in sync here purely as the build-time fallback used until that
  // admin-configured value loads (see launch-standalone-provider.ts, patient-standalone-launch.service.ts,
  // launch-provider-in-app.ts, and patient.service.ts, which all resolve their real base URL from Workflow
  // Settings at runtime — Provider_InApp reuses StandaloneBaseUrl rather than having its own field). The
  // FHIRBridge API's Portal:AllowedOrigins must also include this Demo app's own origin for these calls to
  // succeed (CORS).
  fhirbridgeBase: 'http://172.184.140.105:6003',
};
