import { Component, viewChild } from '@angular/core';
import { SqlFamilyDestinationFormComponent } from './sql-family-destination-form.component';
import { SqlFamilyFormApi } from './destination-form-api';

/** Thin SqlServer wrapper around the shared SQL-family engine — see SqlFamilyDestinationFormComponent. */
@Component({
  selector: 'app-sql-server-destination-form',
  standalone: true,
  imports: [SqlFamilyDestinationFormComponent],
  template: `<app-sql-family-destination-form engine="sqlserver" />`,
})
export class SqlServerDestinationFormComponent implements SqlFamilyFormApi {
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
  testConnection(onSettled?: (result: { connected: boolean; tables: import('../../../../services/destination-schema.service').DestinationTable[] }) => void): void {
    this.engineForm().testConnection(onSettled);
  }
}
