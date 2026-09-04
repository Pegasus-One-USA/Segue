/**
 * Per-vendor SMART `system/` scope vocabulary — the portal-side mirror of the backend's
 * VendorScopeCatalog (src/FHIRBridge.Application/Services/VendorScopeCatalog.cs). Keep the two in sync: the
 * backend copy is authoritative at run time (SourceConnectionRuntimeResolver regenerates scopes on every
 * resolve), this one drives the wizard's Scopes preview and the 'Scopes' value saved onto a node, so a drift
 * between them shows the admin a scope string that isn't what actually gets requested.
 *
 * A vendor with no entry here keeps the uniform `{prefix}/{Resource}.{read|rs}` shape, unchanged.
 */

/** Access level (`read` | `rs` | `r`) a vendor spells each resource type's system read scope with. A resource
 *  type absent from a vendor's map has no system read scope on that vendor at all and is omitted from the
 *  generated scope string entirely, rather than requested and rejected. */
export type VendorScopeProfile = {
  readonly readAccessLevelByResourceType: Readonly<Record<string, string>>;
  /** Access level for this vendor's `system/*` wildcard. */
  readonly wildcardReadAccessLevel: string;
};

/**
 * eClinicalWorks (Healow), derived from the practice's own live `/.well-known/smart-configuration`
 * `scopes_supported` (165 `system/` scopes on the FFBJCD staging sandbox, captured 2026-09-02):
 *
 *  - most resources expose the SMARTv1 coarse `.read`;
 *  - nine expose ONLY the SMARTv2 short form `.r` (no `.read`, no `.rs`): Binary, Claim, Coverage, Media,
 *    MedicationDispense, QuestionnaireResponse, RelatedPerson, **ServiceRequest** and Specimen. ServiceRequest is
 *    in FHIRBridge's own MVP1 resource set, so the uniform `.read` suffix produced `system/ServiceRequest.read`
 *    and eCW failed the entire token request with `invalid_grant`;
 *  - the only system wildcard is `system/*.r` — `system/*.read` does not exist;
 *  - resource types eCW advertises no system read scope for are absent below and so are never requested.
 *
 * `Group` is deliberately omitted even though eCW advertises `system/Group.read`: eCW's Backend Authentication
 * guide requires it for bulk (Group `$export`) requests and requires it to be EXCLUDED from Backend Single
 * Patient calls, which is the only eCW backend flow FHIRBridge supports.
 */
const ECLINICALWORKS_SYSTEM_SCOPES: Readonly<Record<string, string>> = {
  AllergyIntolerance: 'read',
  Basic: 'read',
  Binary: 'r',
  CarePlan: 'read',
  CareTeam: 'read',
  ChargeItem: 'read',
  Claim: 'r',
  Condition: 'read',
  Coverage: 'r',
  Device: 'read',
  DiagnosticReport: 'read',
  DocumentReference: 'read',
  Encounter: 'read',
  Goal: 'read',
  Immunization: 'read',
  Location: 'read',
  Media: 'r',
  Medication: 'read',
  MedicationAdministration: 'read',
  MedicationDispense: 'r',
  MedicationRequest: 'read',
  Observation: 'read',
  Organization: 'read',
  Patient: 'read',
  Practitioner: 'read',
  PractitionerRole: 'read',
  Procedure: 'read',
  Provenance: 'read',
  Questionnaire: 'read',
  QuestionnaireResponse: 'r',
  RelatedPerson: 'r',
  ServiceRequest: 'r',
  Specimen: 'r',
};

export const VENDOR_SCOPE_PROFILES: Readonly<Record<string, VendorScopeProfile>> = {
  // Key is the backend SourceSystemType enum member name (see EhrVendor).
  Healow: {
    readAccessLevelByResourceType: ECLINICALWORKS_SYSTEM_SCOPES,
    wildcardReadAccessLevel: 'r',
  },
};

/** The vendor's `system/` scope profile, or null when it accepts the uniform version-suffix shape. */
export function vendorScopeProfile(vendor: string): VendorScopeProfile | null {
  return VENDOR_SCOPE_PROFILES[vendor] ?? null;
}
