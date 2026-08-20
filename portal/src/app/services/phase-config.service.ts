import { Injectable, signal } from '@angular/core';

// ── Phase Configuration ────────────────────────────────────────────────────────
// Controls which pipeline-step transforms and rank categories are visible in the Node Library.
// Source/destination-TYPE implemented status ("does Cerner have a real wizard yet", "does Aidbox
// have a real wizard yet") now lives in the canonical Node Catalog (see
// NodeCatalogMetadata.Entry.Implemented on the backend) instead of here — this service no longer
// knows about source/destination ids at all. To enable a pipeline step in a future phase, add its
// id to enabledTransformIds — no other code change needed.

export interface PhaseConfig {
  /**
   * Pipeline-step transform IDs (Field Mapping, Audit & Lineage, HEDIS, ...) that are enabled and
   * selectable. Items NOT in this list are hidden entirely, not shown disabled. Destination-TYPE
   * ids no longer belong here — see NodeCatalogMetadata.Entry.Implemented instead.
   */
  enabledTransformIds: string[];

  /**
   * Entire rank-level categories to hide from the Node Library.
   * Rank 2=Validation, 3=Normalize, 4=Terminology, 5=De-identify.
   */
  hiddenRanks: number[];
}

// ── Phase 1 ───────────────────────────────────────────────────────────────────
// No pipeline-step transforms are enabled yet (Field Mapping, Audit & Lineage, HEDIS, Anomaly,
// Patient Aggregation are all Phase 2+) — same as before this file stopped also tracking
// source/destination-type rollout. Validation/Normalize/Terminology/De-identify stay hidden by rank.
const PHASE_1_CONFIG: PhaseConfig = {
  enabledTransformIds: [
    // Phase 2+: 'field-mapping', 'audit-lineage', 'fhir-validation', 'normalize', 'patient-matching',
    //           'merge-patients', 'terminology', 'deid-safeharbor', 'deid-kanon'
    // Phase 2+ analytics: 'hedis', 'anomaly', 'patient-agg'
  ],

  hiddenRanks: [
    2,   // Validation
    3,   // Normalize
    4,   // Terminology
    5,   // De-identify
    // Phase 2+: remove rank numbers from this array to re-enable
  ],
};

// ── Service ───────────────────────────────────────────────────────────────────
@Injectable({ providedIn: 'root' })
export class PhaseConfigService {
  private readonly _config = signal<PhaseConfig>(PHASE_1_CONFIG);

  /** The active phase configuration (reactive). */
  readonly config = this._config.asReadonly();

  /** True if the given pipeline-step transform ID is visible in the current phase. */
  isTransformEnabled(id: string): boolean {
    return this._config().enabledTransformIds.includes(id);
  }

  /** True if an entire rank-level category should be hidden. */
  isRankHidden(rank: number): boolean {
    return this._config().hiddenRanks.includes(rank);
  }

  /**
   * Override the active phase config at runtime.
   * Useful for admin tools, E2E tests, or demo toggles.
   */
  setConfig(config: Partial<PhaseConfig>): void {
    this._config.update(c => ({ ...c, ...config }));
  }
}
