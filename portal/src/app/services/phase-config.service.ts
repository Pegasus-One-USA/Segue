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

  /**
   * Fabric landing modes offered in the Microsoft Fabric destination form.
   *
   * Separate from enabledTransformIds because this gates a CHOICE INSIDE one destination, not the destination
   * itself: OneLake Files is verified and shipping, while the Warehouse COPY INTO path is complete but has never
   * been run against a real Fabric tenant. Listing only the verified mode keeps the unverified one out of users'
   * hands without holding back the whole destination.
   */
  enabledFabricModes: string[];
}

// ── Phase 1 ───────────────────────────────────────────────────────────────────
// Sources:      Epic, Athenahealth, eClinicalWorks
// Categories:   Destination visible; Field Mapping/Validation/Normalize/
//               Terminology/De-identify hidden
// Destinations: SQL Server + CSV + MySQL + PostgreSQL + MongoDB + FHIR Repository (Aidbox) + Medplum +
//               Azure FHIR Service + Azure Blob + Data Lake Webhook only
const PHASE_1_CONFIG: PhaseConfig = {
  enabledSourceIds: [
    'epic',
    'athena',
    'healow',
    // Phase 2+: 'generic-fhir', 'cerner', 'allscripts', 'meditech', 'hl7v2', 'sample'
  ],

  enabledTransformIds: [
    // Destinations — Phase 1
    'dest-sqlserver',
    'dest-csv',
    'dest-mysql',
    'dest-mongo',
    'dest-postgres',
    'dest-medplum',
    'dest-fhir',
    'dest-azurefhir',
    'dest-blob',
    // Lake destinations — backend writers, Step 1 forms and canvas wizard families are all in place
    // (see MappedDataLakeWebhookDestinationWriter / MappedDataFabricDestinationWriter).
    'dest-datalake-webhook',
    // Microsoft Fabric (OneLake Files) — writer, node executor, catalog entry, Step 1 form and wizard
    // family are all in place and registered (see MappedDataFabricDestinationWriter).
    // The vendor heading must be enabled too, not just its surfaces: it is filtered by the same
    // allowlist, and a filtered-out heading takes its children with it (see filteredCategories).
    'dest-fabric-group',
    'dest-fabric',
    'dest-fabric-warehouse',
    // General-purpose, fully configurable outbound REST API — writer, sender, validator, Step 1 form and
    // wizard family are all in place (see MappedApiEndpointDestinationWriter).
    'dest-apiendpoint',
    // Phase 2+: 'field-mapping', 'audit-lineage', 'fhir-validation', 'normalize', 'patient-matching',
    //           'merge-patients', 'terminology', 'deid-safeharbor', 'deid-kanon'
    // Phase 2+ destinations: 'dest-azuresql',
    //   'dest-snowflake', 'dest-powerbi', 'dest-tableau', 'dest-databricks',
    //   'dest-s3', 'dest-xlsx', 'dest-ndjson',
    //   'dest-parquet', 'dest-avro', 'dest-protobuf', 'dest-pdf', 'dest-sftp',
    //   'dest-restapi', 'dest-inmemory'
    // Phase 2+ analytics: 'hedis', 'anomaly', 'patient-agg'
  ],

  // OneLake Files and Warehouse are both verified end to end against a live Fabric tenant. Warehouse requires a
  // pre-created target table: FHIRBridge never creates or alters destination schema.
  //
  // 'lakehouseTable' (Delta) is deliberately NOT listed yet. It is implemented — it writes the _delta_log that
  // registers a Tables/ folder as a real table — but no table written by it has been opened in Fabric, and a
  // Delta log that a reader rejects fails in a particularly unhelpful way: the write reports success and the
  // table simply never appears. Add it here once a live write has been confirmed, the same way Warehouse was
  // held back until it had been.
  enabledFabricModes: ['oneLakeFiles', 'warehouseTable'],

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

  /** True if the given Fabric landing mode is offered in the current phase. */
  isFabricModeEnabled(mode: string): boolean {
    return this._config().enabledFabricModes.includes(mode);
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
