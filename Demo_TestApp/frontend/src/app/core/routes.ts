// Shared by app.ts (which owns routing/redirects) and any DemoType component that needs to build a URL pointing at
// another Login Type's screen — e.g. the demo-type-1 dashboard mockup's "Connect Get Data" redirect into
// LaunchStandalonePatientComponent. Kept in its own module (not exported from app.ts directly) to avoid a circular
// import: app.ts already imports every DemoType component, so a DemoType component importing back from app.ts
// would create a cycle.
export const PATIENT_STANDALONE_PATH = '/launchpatientstandalone';
