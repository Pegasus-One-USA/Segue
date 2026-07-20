export const environment = {
  production: true,
  // QA decouples the portal from Gateway onto its own port (3000), so unlike environment.prod.ts
  // this can't be same-origin/empty — it must point at this environment's own Gateway instance
  // (fhirbridge-qa-gateway, port 3003). Gateway's Portal:AllowedOrigins for this environment's Api
  // must include this portal's own origin (http://localhost:3000) for CORS to succeed.
  apiBase: 'http://localhost:3003',
};
