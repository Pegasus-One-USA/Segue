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

// A separate, dedicated workflow for the per-patient detail fetch (triggered by clicking a row in the fetched
// patient list) — deliberately not PATIENT_WORKFLOW_ID. Same public-launch/ApplicationType.Patient opt-in
// requirements apply to this workflow independently.
export const PATIENT_DETAIL_WORKFLOW_ID = 'dc666cee-f674-400f-a4b5-c826e3f4c3a0';

// Dedicated workflow behind the "Download Patient Information" button — its CSV destination uses Download-URL
// delivery, so a successful /run returns a signed link in outputsByNodeId (see extractDownloadUrl) instead of
// resource JSON. Independent public-launch opt-in from the two workflow ids above.
export const CSV_EXPORT_WORKFLOW_ID = '646ae6c8-c3d8-45ed-a2fb-284bc18842c8';

// Dedicated workflow behind the "Email Patient Information" button — same Source/Mapping shape as
// CSV_EXPORT_WORKFLOW_ID, but its CSV destination uses Email delivery instead of Download-URL, so a successful /run
// never returns a link at all; the file is attached and sent server-side, and this app only needs to know the run
// succeeded. Independent public-launch opt-in from the workflow ids above.
export const CSV_EMAIL_EXPORT_WORKFLOW_ID = 'f85d552d-cc6e-4f4a-8481-a30b1deb3de3';
