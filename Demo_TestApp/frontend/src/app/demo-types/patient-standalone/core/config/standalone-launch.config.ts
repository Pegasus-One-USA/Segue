// Gitignored placeholder, own copy of provider-standalone/core/config/standalone-launch.config.ts's pattern — not
// imported from it, so Patient Standalone and Provider Standalone each carry their own workflow id/base URL without
// touching each other's files. PATIENT_WORKFLOW_ID is the raw id of a FHIRBridge workflow whose Source node uses
// ApplicationType.Patient and that an admin has opted into IsPubliclyLaunchable (POST
// /api/v1/workflows/{id}/enable-public-launch — see the portal's Workflows list, the globe icon). Exposing the raw
// id here is safe: FHIRBridge's anonymous public-patient-standalone-url endpoint refuses to mint a launch context
// for any workflow that isn't explicitly opted in, refuses any workflow whose ApplicationType isn't Patient, and
// only accepts an ehrEndpointId from the same MyChart-only set the hospital picker itself lists — replace the
// placeholder once that opt-in has been done; until then "Select hospital" will 404.
export const FHIRBRIDGE_BASE_URL = 'http://localhost:5000';
export const PATIENT_WORKFLOW_ID = '5930ff91-9235-4ef1-8e94-8752651d4d2a';
