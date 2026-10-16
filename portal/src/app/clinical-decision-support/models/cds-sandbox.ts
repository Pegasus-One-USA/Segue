/**
 * The public CDS Hooks sandbox (https://sandbox.cds-hooks.org) — a free test EHR that calls services from the
 * browser. Registering it as a trusted issuer lets an admin try a service end to end without a real EHR.
 */
export const CDS_SANDBOX = {
  url: 'https://sandbox.cds-hooks.org',
  name: 'CDS Hooks sandbox',
  vendor: 'Other',
  issuer: 'https://sandbox.cds-hooks.org',
  jwksUrl: 'https://sandbox.cds-hooks.org/.well-known/jwks.json',
} as const;

/** Suggestions for the free-text vendor field. */
export const CDS_VENDOR_SUGGESTIONS = ['Epic', 'Oracle Health (Cerner)', 'athenahealth', 'MEDITECH', 'eClinicalWorks', 'Other'];
