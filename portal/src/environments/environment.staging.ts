export const environment = {
  production: true,
  // Staging decouples the portal from Gateway onto its own port (2000), so unlike environment.prod.ts
  // this can't be same-origin/empty — it must point at this environment's own Gateway instance
  // (fhirbridge-staging-gateway, port 2003). Gateway's Portal:AllowedOrigins for this environment's
  // Api must include this portal's own origin (http://localhost:2000) for CORS to succeed.
  apiBase: 'http://localhost:2003',
};
