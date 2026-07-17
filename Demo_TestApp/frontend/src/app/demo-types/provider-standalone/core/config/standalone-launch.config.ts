import { environment } from '../../../../../environments/environment';

// Gitignored — not committed (see Demo_TestApp/frontend/.gitignore). Recreate this file locally
// with real values before building Provider Standalone; this checked-in copy only exists because
// the file was missing entirely on this machine when Demo_TestApp was brought onto this branch.
//
// STANDALONE_WORKFLOW_ID / STANDALONE_DETAIL_WORKFLOW_ID: raw ids of FHIRBridge workflows whose
// Source node uses ApplicationType.Provider and that an admin has opted into IsPubliclyLaunchable
// (POST /api/v1/workflows/{id}/enable-public-launch — see the portal's Workflows list, the globe
// icon) — same requirement patient-standalone/core/config/standalone-launch.config.ts documents
// for its own workflow ids. Replace both placeholders with real opted-in workflow ids.
export const FHIRBRIDGE_BASE_URL = environment.fhirbridgeBase;
export const STANDALONE_WORKFLOW_ID = '<<REPLACE_WITH_REAL_WORKFLOW_ID>>';
export const STANDALONE_DETAIL_WORKFLOW_ID = '<<REPLACE_WITH_REAL_WORKFLOW_ID>>';
