import { ModeConfigSchema } from '../models/mode-config.model';

export const EPIC_MODE_CONFIG: Record<string, ModeConfigSchema> = {
  search: {
    title: 'Search configuration',
    note: (ctx: string) =>
      ctx.startsWith('Patient')
        ? 'Patient-standalone context: searches are scoped to the launched patient by Epic. Patient-level filters are not set here.'
        : 'Provider/backend context: full system-level search parameters are available.',
    callouts: [],
    fields: [
      { key: 'Resource types',  label: 'Resource types to poll',  type: 'text',     def: 'Patient, Observation, Condition, MedicationRequest', hint: 'Comma-separated R4 types (must be within your granted scopes).' },
      { key: 'Search filter',   label: 'Search filter',           type: 'textarea', def: '_lastUpdated=gt{checkpoint}', hint: 'Appended to each search. {checkpoint} = last successful run.', patientHide: true },
      { key: 'Page size',       label: 'Page size (_count)',       type: 'number',   def: '100', hint: 'Epic caps _count per resource type.' },
      { key: 'Poll interval',   label: 'Poll interval',           type: 'select',   def: '15 min', options: ['1 min', '5 min', '15 min', '1 hour', '6 hours', 'Daily', 'Manual'] },
      { key: 'Cursor strategy', label: 'Incremental cursor',      type: 'select',   def: '_lastUpdated watermark', options: ['_lastUpdated watermark', 'Full re-pull each run', 'Since last bundle id'] },
    ],
  },
  export: {
    title: 'Bulk export ($export) configuration',
    note: () => 'Segue kicks off $export, polls the status endpoint, then streams the NDJSON output files.',
    callouts: [
      { type: 'warn', icon: '⚠', html: '<b>Epic constrains bulk export.</b> System-level <code>/$export</code> is generally not available; Epic supports <b>Group-level</b> export behind a Bulk Data-registered client, and the Group must be provisioned by the customer organization.' },
    ],
    fields: [
      { key: 'Export level',         label: 'Export level',               type: 'select', def: 'Group ($export)', options: ['Group ($export)', 'Patient ($export)', 'System ($export) — rarely enabled'], hint: 'Group is the realistic Epic path.' },
      { key: 'Group ID',             label: 'Group ID',                   type: 'text',   def: '', required: true, hint: 'Epic Group resource id (provisioned by the customer).', showIf: { key: 'Export level', equals: 'Group ($export)' } },
      { key: '_type filter',         label: 'Resource types (_type)',      type: 'text',   def: 'Patient,Observation,Condition', hint: 'Comma-separated; empty = all supported types.' },
      { key: '_since watermark',     label: 'Since (_since)',              type: 'text',   def: '{lastRunIso}', hint: 'Only resources changed after this instant.' },
      { key: 'Output handling',      label: 'NDJSON output handling',      type: 'select', def: 'Stage to Blob then process', options: ['Stream to pipeline', 'Stage to Blob then process', 'Stage to S3 then process'] },
      { key: 'Status poll interval', label: 'Kickoff status poll',         type: 'select', def: '30 sec', options: ['10 sec', '30 sec', '1 min', '5 min'] },
    ],
  },
  subscription: {
    title: 'Subscription configuration',
    note: () => 'Segue registers a notification subscription on Epic; Epic POSTs to your callback URL.',
    callouts: [
      { type: 'warn', icon: '⚠', html: '<b>Epic\'s R4 Subscription support is narrow.</b> It is topic/use-case scoped, not arbitrary FHIR search criteria. Confirm the customer\'s Epic supports the topic before relying on this mode.' },
    ],
    fields: [
      { key: 'Callback URL',       label: 'Callback URL (notification endpoint)', type: 'text',     def: 'https://ingress.fhirbridge.io/hooks/epic', required: true, hint: 'Your Segue endpoint Epic will POST to.' },
      { key: 'Subscription topic', label: 'Subscription topic / criteria',        type: 'textarea', def: 'Observation (vital-signs topic)', hint: 'Use an Epic-supported topic, not a free FHIR search.' },
      { key: 'Channel payload',    label: 'Channel payload type',                 type: 'select',   def: 'id-only', options: ['id-only', 'full-resource', 'empty'] },
      { key: 'Callback secret',    label: 'Callback shared secret',               type: 'text',     def: '', required: true, hint: 'Header/secret Epic includes so Segue can verify inbound calls.' },
      { key: 'Heartbeat period',   label: 'Heartbeat period',                     type: 'select',   def: 'None', options: ['None', '5 min', '1 hour', 'Daily'] },
    ],
  },
  webhook: {
    title: 'Webhook receiver configuration',
    note: () => 'Passive receiver for callbacks registered out-of-band. Segue validates each inbound notification.',
    callouts: [],
    fields: [
      { key: 'Listener URL',        label: 'Listener URL (your endpoint)',     type: 'text',   def: 'https://ingress.fhirbridge.io/hooks/epic-webhook', required: true, hint: 'The Segue URL the notifier calls.' },
      { key: 'Verification secret', label: 'Verification / signing secret',    type: 'text',   def: '', required: true, hint: 'Validates the signature/header on inbound calls.' },
      { key: 'On receipt',          label: 'On receipt',                       type: 'select', def: 'Fetch + verify', options: ['Fetch on notify', 'Trust payload', 'Fetch + verify'], hint: 'Re-read from Epic vs trust the pushed payload.' },
      { key: 'Allowed source IPs',  label: 'Allowed source IPs',               type: 'text',   def: '', hint: 'Optional CIDR allowlist for the notifier\'s origin.' },
    ],
  },
};
