export const environment = {
  production: true,
  // Empty on purpose: this app's own backend now serves this build directly (same origin) —
  // see Demo_TestApp/backend/Program.cs's UseStaticFiles()/MapFallbackToFile().
  healthAppBase: '',
  // Must be set to the deployed FHIRBridge.Gateway's real public origin before building for
  // production (e.g. 'https://<server-hostname-or-ip>') — this app calls the real FHIRBridge API
  // directly, cross-origin from wherever this Demo app is hosted, so it needs an absolute URL.
  // Left blank rather than guessed; the FHIRBridge API's Portal:AllowedOrigins must also include
  // this Demo app's own origin for these calls to succeed (CORS).
  fhirbridgeBase: 'http://localhost:5000',
};
