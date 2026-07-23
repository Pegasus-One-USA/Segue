export const environment = {
  production: true,
  // Staging decouples demoapp-portal from demoapp-api onto its own port (2010), so unlike
  // environment.prod.ts neither base can be same-origin/empty. healthAppBase points at this
  // environment's own demo backend (fhirbridge-staging-demoapp-api, port 2011); fhirbridgeBase
  // points at this environment's own FHIRBridge Gateway (fhirbridge-staging-gateway, port 2003).
  // The FHIRBridge Api's Portal:AllowedOrigins for this environment must include this portal's own
  // origin (http://localhost:2010), and the demo backend's AllowedFrontendOrigin must too.
  healthAppBase: 'http://localhost:2011',
  fhirbridgeBase: 'http://localhost:2003',
};
