export const environment = {
  production: true,
  // Dev decouples the portal from Gateway onto its own port (4000), so unlike environment.prod.ts
  // this can't be same-origin/empty — it must point at this environment's own Gateway instance
  // (fhirbridge-dev-gateway, port 4003). Gateway's Portal:AllowedOrigins for this environment's Api
  // must include this portal's own origin (http://localhost:4000) for CORS to succeed.
  apiBase: 'http://localhost:4003',
};
