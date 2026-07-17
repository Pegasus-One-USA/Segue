import { environment } from '../../../../../environments/environment';

// Gitignored — not committed (see Demo_TestApp/frontend/.gitignore): "the FHIRBridge launch-context
// token is a credential, not a checked-in constant." Recreate this file locally with a real
// PROVIDER_LAUNCH_CONTEXT before building demo-type-2; this checked-in copy only exists because the
// file was missing entirely on this machine when Demo_TestApp was brought onto this branch.
//
// PROVIDER_LAUNCH_CONTEXT here is only a build-time fallback — it's admin-editable at runtime through
// launch-provider-in-app.ts's Settings gear (persisted server-side via Demo_TestApp's own backend,
// GET/POST /api/provider-in-app-settings), so a real deployment never needs to rebuild the frontend to
// change it. This constant only matters until an admin has saved a value through that UI.
export const FHIRBRIDGE_BASE_URL = environment.fhirbridgeBase;
export const PROVIDER_LAUNCH_CONTEXT = '<<REPLACE_WITH_REAL_LAUNCH_CONTEXT_TOKEN>>';
