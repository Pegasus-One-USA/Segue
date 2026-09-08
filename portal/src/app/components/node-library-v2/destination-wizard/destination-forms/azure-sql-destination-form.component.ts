import { Component, viewChild } from '@angular/core';
import { SqlFamilyDestinationFormComponent } from './sql-family-destination-form.component';
import { SqlFamilyFormApi } from './destination-form-api';
import { DestinationTable } from '../../../../services/destination-schema.service';

/**
 * Thin AzureSql wrapper around the shared SQL-family engine — see SqlFamilyDestinationFormComponent. AzureSql
 * had zero existing UI anywhere prior to this refactor; it's covered here by the same engine used for
 * SqlServer (identical fields/connection-string shape — buildSqlConnectionString only branches on
 * postgres/mysql/else), differing only in the DestinationType it's saved under.
 */
@Component({
  selector: 'app-azure-sql-destination-form',
  standalone: true,
  imports: [SqlFamilyDestinationFormComponent],
  template: `<app-sql-family-destination-form engine="azuresql" />`,
})
export class AzureSqlDestinationFormComponent implements SqlFamilyFormApi {
  private readonly engineForm = viewChild.required(SqlFamilyDestinationFormComponent);

  get sqlTables() { return this.engineForm().sqlTables; }
  get probeState() { return this.engineForm().probeState; }
  get probeError() { return this.engineForm().probeError; }

  isValid(): boolean { return this.engineForm().isValid(); }
  getRawValue(): Record<string, unknown> { return this.engineForm().getRawValue(); }
  getFullConfig(): Record<string, string> { return this.engineForm().getFullConfig(); }
  getMetadata() { return this.engineForm().getMetadata(); }
  patchFrom(fields: Record<string, string>, target?: string | null): void { this.engineForm().patchFrom(fields, target); }
  reset(): void { this.engineForm().reset(); }
  resetProbe(): void { this.engineForm().resetProbe(); }
  getProbeRequest() { return this.engineForm().getProbeRequest(); }
  testConnection(onSettled?: (result: { connected: boolean; tables: DestinationTable[] }) => void): void {
    this.engineForm().testConnection(onSettled);
  }
}
