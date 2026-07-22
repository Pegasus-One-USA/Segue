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

export const RESOURCE_DEPENDENCIES: Record<string, ResourceDependencyRule> = {
  Patient:                  { required: [],                          recommended: [] },
  Practitioner:             { required: [],                          recommended: [] },
  Encounter:                { required: ['Patient'],                 recommended: ['Practitioner'] },
  Observation:              { required: ['Patient'],                 recommended: ['Encounter', 'Practitioner'] },
  Condition:                { required: ['Patient'],                 recommended: ['Encounter'] },
  Procedure:                { required: ['Patient'],                 recommended: ['Encounter', 'Practitioner'] },
  DiagnosticReport:         { required: ['Patient', 'Observation'],   recommended: ['Encounter'] },
  MedicationRequest:        { required: ['Patient'],                 recommended: ['Practitioner', 'Encounter'] },
  MedicationAdministration: { required: ['Patient', 'MedicationRequest'], recommended: ['Encounter'] },
  ServiceRequest:           { required: ['Patient'],                 recommended: ['Practitioner', 'Encounter'] },
  AllergyIntolerance:       { required: ['Patient'],                 recommended: [] },
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
