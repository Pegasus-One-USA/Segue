export type EpicAudience = 'provider-ehr-launch' | 'provider-standalone' | 'backend-system' | 'patient';

// ── Per-audience field visibility/requirement registry ─────────────────────────
// Adding a new audience means adding one entry here — no template/validator edits.
// All four audiences now show a connection form; only the redirect/launch/retrieval
// shape differs between them.
//
// Lives in its own file (rather than ehr-vendor-source-form.component.ts) so WizardServiceV2 can read it too —
// WizardServiceV2.save()'s entity-mode branch needs to know whether the selected audience wants an `interactive`
// or `retrieval` payload without importing the component itself, which would create a circular dependency
// (the component already injects WizardServiceV2).
export interface AudienceFieldConfig {
  showLaunchUrl: boolean;
  showRedirect: boolean;
  showCdsHooks: boolean;
  /** Whether this app's registered EHR launch-display setting (Embedded/External Browser/Sidebar) applies.
   *  Only meaningful for the EHR-launch audience — FHIRBridge doesn't control this behavior, the EHR does; this
   *  just records how the app was registered there. */
  showLaunchDisplayMode: boolean;
  /** Whether the retrieval-method section applies at all. */
  showRetrieval: boolean;
  /**
   * How much of the retrieval section this audience gets:
   * - 'none': no retrieval section (EHR launch / patient — data arrives via the SMART launch context).
   * - 'oneshot': a curated Search REST subset (Resource Types, Search Criteria, Max Results, Include Related
   *   Resources) for a user-initiated, single fetch — no scheduler, since there's no recurring run to schedule.
   * - 'automated': the full retrieval method picker + config (Backend System) for unattended, recurring execution.
   */
  retrievalScope: 'none' | 'oneshot' | 'automated';
  /** Whether the shared Resource Type picker (Section 5) applies. False for Backend System,
   *  where Resource Type instead lives inside the selected retrieval method's own config —
   *  never both, to avoid showing two Resource Type pickers at once. */
  showResourcePicker: boolean;
  /** 'readonly' = auto-populated Redirect URI (providers); 'editable' = mandatory Callback URL (patient). */
  redirectMode: 'readonly' | 'editable';
  redirectLabel: string;
  scopePrefix: 'user' | 'patient' | 'system';
  /** Interactive audiences add openid/fhirUser/offline_access/launch to the scope string; Backend System does not. */
  includeInteractiveScopes: boolean;
}

import { EhrVendor } from '../../../ehr-endpoints/models/ehr-endpoint.model';

/**
 * Audiences hidden (not deleted — a re-enable is a one-line edit) for a given vendor, on top of the vendor-blind
 * AUDIENCE_FIELD_CONFIG above. Currently only athenahealth: Provider standalone and EHR launch are fully
 * implemented server-side (SmartAuthorizationCodeTokenProvider is vendor-neutral) but round-trip-unverified —
 * sandbox provider credentials don't exist yet. Backend and Patient are both verified against the live preview
 * sandbox and stay enabled.
 */
export const VENDOR_DISABLED_AUDIENCES: Partial<Record<EhrVendor, EpicAudience[]>> = {
  Athenahealth: ['provider-standalone', 'provider-ehr-launch'],
  // eClinicalWorks (Healow): Patient, Provider EHR launch, AND Backend System are rolled out. Provider EHR launch
  // (Provider EMR) was verified end-to-end against the live eCW sandbox (poc/ecw-ehr-launch-poc: EHR launch → PKCE →
  // confidential client_secret_basic token → multi-resource FHIR reads). Backend System was verified end-to-end
  // against the live eCW sandbox (SMART Backend Services: RS384 private_key_jwt → client_credentials token →
  // Group/{id}/$export → poll → NDJSON; see docs/backend/17-ecw-backend-bulkexport-integration-plan.md). Provider
  // standalone stays disabled until its own sandbox credentials/round-trip verification exist — re-enable when verified.
  Healow: ['provider-standalone'],
};

export function isAudienceDisabledForVendor(vendor: EhrVendor, audience: EpicAudience): boolean {
  return VENDOR_DISABLED_AUDIENCES[vendor]?.includes(audience) ?? false;
}

export const AUDIENCE_FIELD_CONFIG: Record<EpicAudience, AudienceFieldConfig> = {
  // CDS Hooks removed from the UI (not required) — flag kept for future use but disabled everywhere.
  'provider-ehr-launch': { showLaunchUrl: true,  showRedirect: true,  showCdsHooks: false, showRetrieval: false, retrievalScope: 'none',     showResourcePicker: true,  redirectMode: 'editable', redirectLabel: 'Redirect URI', scopePrefix: 'user',    includeInteractiveScopes: true,  showLaunchDisplayMode: true },
  // showLaunchUrl: false — per SMART App Launch, only EHR Launch has a "launch_uri" the EHR calls to *initiate*
  // the sequence from inside itself. Standalone is launched independently by the user; it only ever needs a
  // Redirect URI (OAuth callback), never a Launch URL.
  'provider-standalone': { showLaunchUrl: false, showRedirect: true,  showCdsHooks: false, showRetrieval: true,  retrievalScope: 'oneshot',  showResourcePicker: true,  redirectMode: 'editable', redirectLabel: 'Redirect URI', scopePrefix: 'user',    includeInteractiveScopes: true,  showLaunchDisplayMode: false },
  'patient':             { showLaunchUrl: false, showRedirect: true,  showCdsHooks: false, showRetrieval: false, retrievalScope: 'none',     showResourcePicker: true,  redirectMode: 'editable', redirectLabel: 'Callback URL', scopePrefix: 'patient', includeInteractiveScopes: true,  showLaunchDisplayMode: false },
  'backend-system':      { showLaunchUrl: false, showRedirect: false, showCdsHooks: false, showRetrieval: true,  retrievalScope: 'automated', showResourcePicker: false, redirectMode: 'readonly', redirectLabel: '',             scopePrefix: 'system',  includeInteractiveScopes: false, showLaunchDisplayMode: false },
};
