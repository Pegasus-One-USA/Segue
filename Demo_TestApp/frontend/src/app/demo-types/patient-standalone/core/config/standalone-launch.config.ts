// Dedicated workflow behind the "Download Patient Information" button — its CSV destination uses Download-URL
// delivery, so a successful /run returns a signed link in outputsByNodeId (see extractDownloadUrl) instead of
// resource JSON. Independent public-launch opt-in from the workflow ids WorkflowSettingsEntity holds (see
// PatientStandaloneLaunchService.loadConfig / launch-standalone-patient.ts).
export const CSV_EXPORT_WORKFLOW_ID = '646ae6c8-c3d8-45ed-a2fb-284bc18842c8';

// Dedicated workflow behind the "Email Patient Information" button — same Source/Mapping shape as
// CSV_EXPORT_WORKFLOW_ID, but its CSV destination uses Email delivery instead of Download-URL, so a successful /run
// never returns a link at all; the file is attached and sent server-side, and this app only needs to know the run
// succeeded. Independent public-launch opt-in from the workflow ids above.
export const CSV_EMAIL_EXPORT_WORKFLOW_ID = 'f85d552d-cc6e-4f4a-8481-a30b1deb3de3';
