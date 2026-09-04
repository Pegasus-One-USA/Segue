import { IngestionMode, IngestionGate } from '../models/ingestion-mode.model';

export const INGESTION_MODES: IngestionMode[] = [
  { id: 'search',       name: 'Search resources (R4)', acid: true, sub: 'Scheduled REST search with pagination.' },
  { id: 'export',       name: 'Bulk export ($export)',             sub: 'Async FHIR Bulk Data; stream NDJSON output.' },
  { id: 'subscription', name: 'Register subscription',             sub: 'Register a notification subscription on Epic.' },
  { id: 'webhook',      name: 'Receive webhook',                   sub: 'Passive listener for pushed notifications.' },
];

export const EPIC_INGESTION: Record<string, IngestionGate> = {
  'Patient (standalone)':  { allowed: ['search'],                                  default: 'search' },
  'Provider (standalone)': { allowed: ['search'],                                  default: 'search' },
  'Provider (EHR launch)': { allowed: ['search'],                                  default: 'search' },
  'Backend system':        { allowed: ['export', 'search', 'subscription', 'webhook'], default: 'export' },
};
