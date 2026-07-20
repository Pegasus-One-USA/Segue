export const environment = {
  production: true,
  // QA decouples demoapp-portal from demoapp-api onto its own port (3010), so unlike
  // environment.prod.ts neither base can be same-origin/empty. healthAppBase points at this
  // environment's own demo backend (fhirbridge-qa-demoapp-api, port 3011); fhirbridgeBase points at
  // this environment's own FHIRBridge Gateway (fhirbridge-qa-gateway, port 3003).
  // The FHIRBridge Api's Portal:AllowedOrigins for this environment must include this portal's own
  // origin (http://localhost:3010), and the demo backend's AllowedFrontendOrigin must too.
  healthAppBase: 'http://localhost:3011',
  fhirbridgeBase: 'http://localhost:3003',
};
