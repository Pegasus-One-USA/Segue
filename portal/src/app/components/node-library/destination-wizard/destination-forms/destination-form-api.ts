import { Signal } from '@angular/core';
import { DestinationConfigFormComponent } from '../../../shared/config-form/config-form.contract';
import { DestinationProbeRequest, DestinationTable } from '../../../../services/destination-schema.service';

/**
 * Superset of DestinationConfigFormComponent implemented by every component under destination-forms/ that
 * DestinationWizardComponent's Step 1 and DestinationConnectionFormComponent host dynamically — lets those two
 * hosts do everything the old inline sqlForm/csvForm/mongoForm did (validity gating, existing-connection
 * diffing, re-populating from a saved node or a picked existing connection, resetting to blank) without
 * knowing which concrete component the DESTINATION_FORM_REGISTRY lookup resolved to.
 */
export interface WizardDestinationFormApi extends DestinationConfigFormComponent {
  /** Mirrors FormGroup.valid — drives the host's "Next"/"Save" disabled state. */
  isValid(): boolean;
  /** Raw current field values (FormGroup.getRawValue() shape, dest_*-less keys) — used only for
   *  existing-connection diffing (secret-bearing keys are excluded by the caller, same as before). */
  getRawValue(): Record<string, unknown>;
  /** The full dest_*-keyed config bag for the current form — INCLUDES secret-bearing keys
   *  (dest_password/dest_connectionString/dest_sftpPassword), unlike getMetadata()'s non-secret `fields`.
   *  This is what a CanvasNode's own fields bag stores (a later, untouched step strips secrets before real
   *  persistence — see workflow-graph-mapper.service.ts's SECRET_FIELD_KEYS) and what Step 4's review and the
   *  mapping canvas's live schema-mutation calls (Add Column/Create Table) need after Step 1 is behind. */
  getFullConfig(): Record<string, string>;
  /** Patches the form from a previously-saved dest_* config bag — used both by "select an existing
   *  connection" (secrets always blank) and by re-opening a saved canvas node (secrets may be populated).
   *  `target` is the DestinationConfigurationDto's own `target` column, used as a fallback for whichever
   *  field that type maps target onto (CSV's filePattern, Mongo's collection). */
  patchFrom(fields: Record<string, string>, target?: string | null): void;
  /** Back to this form's blank defaults — the "✕ clear existing connection" affordance. */
  reset(): void;
}

/** Extra members exposed only by the SQL-family wrappers (SqlServer/AzureSql/MySql/PostgreSql) — the live
 *  schema probe that Step 1 gates "Next" on and Steps 2-4's mapping canvas consumes afterwards (table/column
 *  pickers, live ALTER TABLE/CREATE TABLE calls). */
export interface SqlFamilyFormApi extends WizardDestinationFormApi {
  readonly sqlTables: Signal<DestinationTable[]>;
  readonly probeState: Signal<'idle' | 'testing' | 'ok' | 'error'>;
  readonly probeError: Signal<string | null>;
  /** Runs (or re-runs) the live connection probe. `onSettled` fires once with the outcome so a caller that
   *  gated "Next" on this can advance immediately on success without a separate reactive watch. */
  testConnection(onSettled?: (result: { connected: boolean; tables: DestinationTable[] }) => void): void;
  /** Back to 'idle' with no tables — used when the wizard's "Back" returns to Step 1, so a stale probe never
   *  silently carries forward across a Back/Next round trip (matches the pre-refactor behavior exactly). */
  resetProbe(): void;
  /** The ad-hoc (not-yet-provisioned) connection details the mapping canvas's live schema-mutation calls
   *  (Add Column/Create Table) need while editing a brand-new, not-yet-saved connection. */
  getProbeRequest(): DestinationProbeRequest;
}

export function isSqlFamilyForm(x: WizardDestinationFormApi | null | undefined): x is SqlFamilyFormApi {
  return !!x && typeof (x as Partial<SqlFamilyFormApi>).testConnection === 'function';
}
