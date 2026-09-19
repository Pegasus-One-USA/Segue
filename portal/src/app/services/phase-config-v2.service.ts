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
   * V2 ranks: 0=Source, 1=Destination, 2=Mapping, 3=Transformation, 4=De-identification
   * (see transforms-v2.data.ts). Every one of those is part of V2's intended chain, so unlike V1 — whose
   * ranks 2–5 were granular Validation/Normalize/Terminology/De-identify steps held back for a later
   * phase — there is nothing to hide here by default.
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
    // V2's chain steps — all three are the point of this builder, so none is phase-gated. (In V1 these
    // were 'field-mapping' plus the granular normalize/terminology/deid-* ids, all held back to a later
    // phase; V2 collapses them into these two consolidated steps — see transforms-v2.data.ts.)
    'field-mapping',
    'transformation',
    'deidentification',
    // Phase 2+ destinations: 'dest-azuresql',
    //   'dest-snowflake', 'dest-powerbi', 'dest-tableau', 'dest-databricks',
    //   'dest-s3', 'dest-xlsx', 'dest-ndjson',
    //   'dest-parquet', 'dest-avro', 'dest-protobuf', 'dest-pdf', 'dest-sftp',
    //   'dest-restapi', 'dest-inmemory'
  ],

  // OneLake Files is verified end to end against a live Fabric tenant. Warehouse (staged Parquet + COPY INTO,
  // MERGE on upsert) is listed alongside it but has NOT had a live write confirmed yet — its shape is the
  // documented one, and the three things most likely to need adjusting on first contact are the COPY INTO
  // credential clause, the abfss staging URL form, and whether the identity needs grants on the staging
  // Lakehouse separately from the Warehouse. Note it also requires a pre-created target table: FHIRBridge
  // never creates or alters destination schema.
  enabledFabricModes: ['oneLakeFiles', 'warehouseTable'],

  hiddenRanks: [],
};

// ── Service ───────────────────────────────────────────────────────────────────
@Injectable({ providedIn: 'root' })
export class PhaseConfigServiceV2 {
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
