export const environment = {
  production: true,
  // Empty on purpose: the portal is served by FHIRBridge.Gateway from the same origin as
  // /api/**, so calls resolve as same-origin relative paths (no CORS, no separate API host).
  apiBase: '',
};
