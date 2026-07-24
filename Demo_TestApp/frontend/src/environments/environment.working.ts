export const environment = {
  production: true,
  // Working decouples demoapp-portal from demoapp-api onto its own port (6010), so unlike
  // environment.prod.ts neither base can be same-origin/empty. healthAppBase points at this
  // environment's own demo backend (fhirbridge-working-demoapp-api, port 6011); fhirbridgeBase
  // points at this environment's own FHIRBridge Gateway (fhirbridge-working-gateway, port 6003).
  // The FHIRBridge Api's Portal:AllowedOrigins for this environment must include this portal's own
  // origin (http://localhost:6010), and the demo backend's AllowedFrontendOrigin must too.
  healthAppBase: 'http://localhost:6011',
  fhirbridgeBase: 'http://localhost:6003',
};
