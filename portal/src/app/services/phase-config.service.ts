import { Injectable, signal } from '@angular/core';

// ── Phase Configuration ────────────────────────────────────────────────────────
// Controls which sources, transform categories, and destinations are visible.
// To enable items in a future phase, flip the flag to `true` — no code changes.

export interface PhaseConfig {
  /** Source connector IDs that are enabled and visible. All others are hidden. */
  enabledSourceIds: string[];

  /**
   * Transform/destination IDs that are enabled and selectable.
   * Items NOT in this list will be shown as greyed-out (disabled) or hidden.
   */
  enabledTransformIds: string[];

  /**
   * Entire rank-level categories to hide from the Node Library.
   * Rank 2=Validation, 3=Normalize, 4=Terminology, 5=De-identify.
   */
  hiddenRanks: number[];
}

// ── Phase 1 ───────────────────────────────────────────────────────────────────
// Sources:      Epic only
// Categories:   Destination visible; Field Mapping/Validation/Normalize/
//               Terminology/De-identify hidden
// Destinations: SQL Server + CSV only
const PHASE_1_CONFIG: PhaseConfig = {
  enabledSourceIds: [
    'epic',
    'generic-fhir',
    // Phase 2+: 'cerner', 'athena', 'allscripts', 'healow', 'meditech', 'hl7v2', 'sample'
  ],

  enabledTransformIds: [
    // Destinations — Phase 1
    'dest-sqlserver',
    'dest-csv',
    // Phase 2+: 'field-mapping', 'audit-lineage', 'fhir-validation', 'normalize', 'patient-matching',
    //           'merge-patients', 'terminology', 'deid-safeharbor', 'deid-kanon'
    // Phase 2+ destinations: 'dest-mysql', 'dest-mongo', 'dest-postgres', 'dest-azuresql',
    //   'dest-snowflake', 'dest-powerbi', 'dest-tableau', 'dest-databricks',
    //   'dest-blob', 'dest-s3', 'dest-fhir', 'dest-xlsx', 'dest-ndjson',
    //   'dest-parquet', 'dest-avro', 'dest-protobuf', 'dest-pdf', 'dest-sftp',
    //   'dest-restapi', 'dest-inmemory'
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

  /** True if the given source connector ID is enabled in the current phase. */
  isSourceEnabled(id: string): boolean {
    return this._config().enabledSourceIds.includes(id);
  }

  /** True if the given transform/destination ID is visible in the current phase. */
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
