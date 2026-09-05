// ── FHIR resource dependency configuration ──────────────────────────────────
// Data-driven so a new resource, or a change to an existing one's dependencies, only ever means editing
// RESOURCE_DEPENDENCIES below — never touching the selection logic in destination-wizard.component.ts.

export interface ResourceDependencyRule {
  /** Auto-selected (and locked against manual deselection) whenever this resource is selected. Resolved
   *  transitively — see requiredClosureFor. */
  required: string[];
  /** Suggested alongside this resource, but never auto-selected and never locked. */
  recommended: string[];
}

const NO_DEPENDENCIES: ResourceDependencyRule = { required: [], recommended: [] };

// Covers the platform's "compartment" types (see backend PatientCompartmentResourceTypes.All) plus Practitioner,
// Organization, and Medication. Manual selection is what actually controls fetching on the backend
// (SourceNodeExecutors) — these `recommended` entries are deliberately hints only (never `required`/auto-locked):
// forcing a companion resource on the user would narrow their control the same way an earlier auto-reference-
// resolution fix did before it was reverted at the user's request (see SourceNodeExecutorDestinationRestrictionTests
// .Non_compartment_type_referenced_by_a_selected_resource_is_excluded_when_not_itself_selected). An unchecked type
// referenced by a checked one is still genuinely left out and can leave a dangling reference at the destination —
// surfaced as a precise per-record block by MappedFhirRepositoryDestinationWriter's reference-resolution logic, not
// prevented here; these hints exist to help a user avoid reaching that block in the first place. Practitioner/
// Organization/Medication are resolved by id when selected (see backend FetchByCollectedReferencesAsync), which is
// why they're hinted here. Location and Specimen (Encounter.location and Observation.specimen/basedOn respectively)
// were confirmed as real, commonly-populated cross-references via live Epic + Aidbox testing and are hinted below
// too. Device, Questionnaire, Schedule, and Slot were reviewed and found to have no patient-relevant cross-reference
// worth hinting from any resource currently in this map — their entries below are a deliberate "reviewed, nothing to
// add" marker, not an oversight.
export const RESOURCE_DEPENDENCIES: Record<string, ResourceDependencyRule> = {
  Patient:                  { required: [],                          recommended: ['Practitioner', 'Organization'] },
  Practitioner:             { required: [],                          recommended: [] },
  Organization:             { required: [],                          recommended: [] },
  Medication:               { required: [],                          recommended: [] },
  Location:                 { required: [],                          recommended: ['Organization'] },
  Specimen:                 { required: ['Patient'],                 recommended: ['Practitioner'] },
  Device:                   { required: [],                          recommended: [] },
  Questionnaire:            { required: [],                          recommended: [] },
  Schedule:                 { required: [],                          recommended: [] },
  Slot:                     { required: [],                          recommended: [] },
  Encounter:                { required: ['Patient'],                 recommended: ['Practitioner', 'Organization', 'Location'] },
  Observation:              { required: ['Patient'],                 recommended: ['Encounter', 'Practitioner', 'ServiceRequest', 'Specimen'] },
  Condition:                { required: ['Patient'],                 recommended: ['Encounter'] },
  Procedure:                { required: ['Patient'],                 recommended: ['Encounter', 'Practitioner'] },
  DiagnosticReport:         { required: ['Patient', 'Observation'],   recommended: ['Encounter'] },
  MedicationRequest:        { required: ['Patient'],                 recommended: ['Practitioner', 'Encounter', 'Medication'] },
  MedicationAdministration: { required: ['Patient', 'MedicationRequest'], recommended: ['Encounter', 'Medication'] },
  ServiceRequest:           { required: ['Patient'],                 recommended: ['Practitioner', 'Encounter'] },
  AllergyIntolerance:       { required: ['Patient'],                 recommended: [] },
  Immunization:             { required: ['Patient'],                 recommended: ['Encounter'] },
  Appointment:              { required: ['Patient'],                 recommended: ['Practitioner'] },
  CarePlan:                 { required: ['Patient'],                 recommended: ['Encounter', 'CareTeam'] },
  CareTeam:                 { required: ['Patient'],                 recommended: ['Practitioner'] },
  Communication:            { required: ['Patient'],                 recommended: ['Encounter', 'Practitioner'] },
  CommunicationRequest:     { required: ['Patient'],                 recommended: ['Encounter', 'Practitioner'] },
  FamilyMemberHistory:      { required: ['Patient'],                 recommended: [] },
  ImagingStudy:             { required: ['Patient'],                 recommended: ['Encounter'] },
  MedicationDispense:       { required: ['Patient'],                 recommended: ['MedicationRequest', 'Medication'] },
  MedicationStatement:      { required: ['Patient'],                 recommended: ['MedicationRequest', 'Medication'] },
  QuestionnaireResponse:    { required: ['Patient'],                 recommended: ['Encounter'] },
  RelatedPerson:            { required: ['Patient'],                 recommended: [] },
  Task:                     { required: [],                          recommended: ['Patient', 'Encounter'] },
};

function ruleFor(resource: string): ResourceDependencyRule {
  return RESOURCE_DEPENDENCIES[resource] ?? NO_DEPENDENCIES;
}

/** Everything `resource` transitively requires (never includes `resource` itself, dedup'd, order not
 *  significant). E.g. MedicationAdministration → [MedicationRequest, Patient] since MedicationRequest
 *  itself also requires Patient. */
export function requiredClosureFor(resource: string): string[] {
  const seen = new Set<string>();
  const stack = [...ruleFor(resource).required];
  while (stack.length) {
    const next = stack.pop()!;
    if (next === resource || seen.has(next)) continue;
    seen.add(next);
    stack.push(...ruleFor(next).required);
  }
  return [...seen];
}

/** Direct recommendations for `resource`, exactly as authored (not transitively expanded). */
export function recommendedFor(resource: string): string[] {
  return ruleFor(resource).recommended;
}

/** Every member of `selected` that transitively requires `resource` — i.e. why `resource` can't be
 *  deselected right now. Empty once every such dependent has been removed from `selected`. */
export function lockingDependentsOf(resource: string, selected: readonly string[]): string[] {
  return selected.filter(r => r !== resource && requiredClosureFor(r).includes(resource));
}

const rankCache = new Map<string, number>();

/**
 * A resource's position in dependency order: 0 for anything with no required dependency (Patient,
 * Practitioner, …), otherwise 1 + the highest rank among its direct required dependencies — so a
 * resource always ranks strictly after everything it (even transitively) requires. `visiting` guards
 * against a future misconfigured cycle in RESOURCE_DEPENDENCIES turning into infinite recursion; a
 * cycle has no correct rank, so it's treated as depth 0 rather than crashing.
 */
export function dependencyRankFor(resource: string, visiting: ReadonlySet<string> = new Set()): number {
  const cached = rankCache.get(resource);
  if (cached !== undefined) return cached;
  if (visiting.has(resource)) return 0;

  const required = ruleFor(resource).required;
  if (!required.length) {
    rankCache.set(resource, 0);
    return 0;
  }
  const nextVisiting = new Set(visiting).add(resource);
  const rank = 1 + Math.max(...required.map(dep => dependencyRankFor(dep, nextVisiting)));
  rankCache.set(resource, rank);
  return rank;
}

/**
 * Orders `resources` so anything another selected resource requires always comes first (Patient before
 * Encounter before DiagnosticReport, …) — the order data groups should actually be run/mapped in.
 * Stable: resources sharing the same rank keep their original relative order (e.g. selection order).
 */
export function sortByDependencyRank(resources: readonly string[]): string[] {
  return [...resources].sort((a, b) => dependencyRankFor(a) - dependencyRankFor(b));
}
