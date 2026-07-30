// Shared by app.ts (which owns routing/redirects) and any demo-experience component that needs to build a URL
// pointing at another role's screen — e.g. the demo-type-1 dashboard mockup's "Connect Get Data" redirect into
// LaunchStandalonePatientComponent. Kept in its own module (not exported from app.ts directly) to avoid a circular
// import: app.ts already imports every demo-experience component, so one importing back from app.ts would create
// a cycle.
export const PATIENT_STANDALONE_PATH = '/launchpatientstandalone';

// Mirrors app.ts's AUTH_ROLE_STORAGE_KEY sessionStorage pattern, but for the logged-in account's email — the
// stable identity FHIRBridge permanently binds a MyChart patient / Epic practitioner to (see
// LaunchStandalonePatientComponent/LaunchStandaloneProviderComponent's userIdentity getters). Kept here rather
// than exported from app.ts directly, for the same circular-import reason as PATIENT_STANDALONE_PATH above.
export const AUTH_EMAIL_STORAGE_KEY = 'hb_auth_email';
