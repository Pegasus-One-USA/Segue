export const environment = {
  production: true,
  // Working decouples the portal from Gateway onto its own port (6000), so unlike environment.prod.ts
  // this can't be same-origin/empty — it must point at this environment's own Gateway instance
  // (fhirbridge-working-gateway, port 6003). Gateway's Portal:AllowedOrigins for this environment's
  // Api must include this portal's own origin (http://localhost:6000) for CORS to succeed.
  apiBase: 'http://localhost:6003',
};
