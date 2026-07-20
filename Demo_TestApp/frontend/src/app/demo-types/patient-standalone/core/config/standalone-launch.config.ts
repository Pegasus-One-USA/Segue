// Dedicated workflow behind the "Download Patient Information" button — its CSV destination uses Download-URL
// delivery, so a successful /run returns a signed link in outputsByNodeId (see extractDownloadUrl) instead of
// resource JSON. Independent public-launch opt-in from the workflow ids WorkflowSettingsEntity holds (see
// PatientStandaloneLaunchService.loadConfig / launch-standalone-patient.ts).
export const CSV_EXPORT_WORKFLOW_ID = 'a0de009e-9a60-494f-9ff8-d83cefdd1a3b';

// Dedicated workflow behind the "Email Patient Information" button — same Source/Mapping shape as
// CSV_EXPORT_WORKFLOW_ID, but its CSV destination uses Email delivery instead of Download-URL, so a successful /run
// never returns a link at all; the file is attached and sent server-side, and this app only needs to know the run
// succeeded. Independent public-launch opt-in from the workflow ids above.
export const CSV_EMAIL_EXPORT_WORKFLOW_ID = 'c5e813f5-04fe-4223-8465-fba1a1e83b75';
