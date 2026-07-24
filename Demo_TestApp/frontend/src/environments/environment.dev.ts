export const environment = {
  production: true,
  // Dev decouples demoapp-portal from demoapp-api onto its own port (4010), so unlike
  // environment.prod.ts neither base can be same-origin/empty. healthAppBase points at this
  // environment's own demo backend (fhirbridge-dev-demoapp-api, port 4011); fhirbridgeBase points at
  // this environment's own FHIRBridge Gateway (fhirbridge-dev-gateway, port 4003).
  // The FHIRBridge Api's Portal:AllowedOrigins for this environment must include this portal's own
  // origin (http://localhost:4010), and the demo backend's AllowedFrontendOrigin must too.
  healthAppBase: 'http://localhost:4011',
  fhirbridgeBase: 'http://localhost:4003',
};
