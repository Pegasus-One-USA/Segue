import { environment } from '../../../../../environments/environment';

// Gitignored — not committed (see Demo_TestApp/frontend/.gitignore): "the FHIRBridge launch-context
// token is a credential, not a checked-in constant." Recreate this file locally with a real
// PROVIDER_LAUNCH_CONTEXT before building demo-type-2; this checked-in copy only exists because the
// file was missing entirely on this machine when Demo_TestApp was brought onto this branch.
export const FHIRBRIDGE_BASE_URL = environment.fhirbridgeBase;
export const PROVIDER_LAUNCH_CONTEXT = '<<REPLACE_WITH_REAL_LAUNCH_CONTEXT_TOKEN>>';
