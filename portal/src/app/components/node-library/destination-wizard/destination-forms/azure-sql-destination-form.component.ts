import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { DestinationSchemaService, DestinationTable, DestinationProbeRequest } from '../../../../services/destination-schema.service';
import { buildConnectionMetadata, buildSqlConnectionString } from '../../../../destination-connections/utils/destination-connection-secret.util';
import { SqlFamilyFormApi } from './destination-form-api';

/**
 * Fully independent AzureSql destination form. This used to be a thin wrapper around a shared
 * SqlFamilyDestinationFormComponent engine driven by an `engine` input; that engine has been fully inlined here
 * so AzureSql's configuration, validation, and API handling are completely independent of SqlServer/PostgreSQL/
 * MySQL — editing this file can never affect any of the other three. Every `this.engine()` call below is
 * unchanged from the original shared implementation, just permanently fixed to 'azuresql'. AzureSql had zero
 * live UI anywhere prior to the original registry refactor; it reuses SqlServer's exact fields/behavior,
 * differing only in the DestinationType/dest_engine tag it's saved under (see _destinationTypeFor below).
 */
@Component({
  selector: 'app-azure-sql-destination-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './azure-sql-destination-form.component.html',
  styleUrls: ['../destination-wizard.component.scss', './azure-sql-destination-form.component.scss'],
})
export class AzureSqlDestinationFormComponent implements SqlFamilyFormApi {
  private readonly fb = inject(FormBuilder);
  private readonly schemaSvc = inject(DestinationSchemaService);

  private readonly engine = (): 'sqlserver' | 'mysql' | 'postgres' | 'azuresql' => 'azuresql';

  readonly showSslToggle = () => this.engine() === 'mysql' || this.engine() === 'postgres';
  readonly hostLabel = () => (this.showSslToggle() ? 'Host' : 'SQL Server');

  readonly sqlForm = this.fb.group({
    name: ['', [Validators.required]],
    server: ['', [Validators.required]],
    database: ['', [Validators.required]],
    auth: ['sql-auth', [Validators.required]],
    username: [''],
    password: [''],
    schema: ['dbo', []],
    writeMode: ['upsert', []],
    // MySQL/PostgreSQL only (see DestinationConnectionProbeRequest.RequireSsl backend-side): off by default so
    // a local/docker instance with SSL disabled still connects; check for managed providers that enforce SSL
    // (e.g. AWS RDS's rds.force_ssl).
    requireSsl: [false, []],
  });

  readonly sqlTables = signal<DestinationTable[]>([]);
  readonly probeState = signal<'idle' | 'testing' | 'ok' | 'error'>('idle');
  readonly probeError = signal<string | null>(null);

  private _destinationTypeFor(): 'SqlServer' | 'AzureSql' | 'MySql' | 'PostgreSql' {
    switch (this.engine()) {
      case 'mysql': return 'MySql';
      case 'postgres': return 'PostgreSql';
      case 'azuresql': return 'AzureSql';
      default: return 'SqlServer';
    }
  }

  isValid(): boolean {
    return this.sqlForm.valid;
  }

  getRawValue(): Record<string, unknown> {
    return this.sqlForm.getRawValue();
  }

  /** Connection-only fields keyed the same way the old DestinationWizardComponent._buildConnectionConfig()/
   *  DestinationConnectionFormComponent.getConfig() kept them — dest_engine records which SQL family this is,
   *  same string DestinationWizardComponent used to compute inline (mysql/postgres/sqlserver); AzureSql is new
   *  and tags itself 'sqlserver' too, since AzureSql's wire-level connection string shape is identical to SQL
   *  Server's (see buildSqlConnectionString, which only branches on postgres/mysql/else).
   */
  getFullConfig(): Record<string, string> {
    const v = this.sqlForm.getRawValue();
    const engine = this.engine();
    const config: Record<string, string> = {
      dest_name: v.name ?? '',
      dest_engine: engine === 'mysql' ? 'mysql' : engine === 'postgres' ? 'postgres' : 'sqlserver',
      dest_server: v.server ?? '',
      dest_database: v.database ?? '',
      dest_auth: v.auth ?? '',
      dest_schema: v.schema ?? 'dbo',
      dest_writeMode: v.writeMode ?? 'upsert',
      dest_requireSsl: String(v.requireSsl ?? false),
    };
    if ((v.auth ?? 'sql-auth') === 'sql-auth') {
      config['dest_username'] = v.username ?? '';
      config['dest_password'] = v.password ?? '';
    }
    return config;
  }

  getMetadata(): { fields: Record<string, string>; secret?: string | null } | null {
    if (!this.isValid()) return null;
    const config = this.getFullConfig();
    return {
      fields: JSON.parse(buildConnectionMetadata(config, 'sql')) as Record<string, string>,
      secret: buildSqlConnectionString(config),
    };
  }

  patchFrom(fields: Record<string, string>, _target?: string | null): void {
    this.sqlForm.patchValue({
      name: fields['dest_name'] || this.sqlForm.value.name || '',
      server: fields['dest_server'] || '',
      database: fields['dest_database'] || '',
      auth: fields['dest_auth'] || 'sql-auth',
      username: fields['dest_username'] || '',
      password: fields['dest_password'] || '',
      schema: fields['dest_schema'] || 'dbo',
      writeMode: fields['dest_writeMode'] || 'upsert',
      requireSsl: fields['dest_requireSsl'] === 'true',
    });
  }

  reset(): void {
    this.sqlForm.reset({
      name: '', server: '', database: '', auth: 'sql-auth', username: '', password: '',
      schema: 'dbo', writeMode: 'upsert', requireSsl: false,
    });
    this.resetProbe();
  }

  resetProbe(): void {
    this.probeState.set('idle');
    this.probeError.set(null);
    this.sqlTables.set([]);
  }

  getProbeRequest(): DestinationProbeRequest {
    const v = this.sqlForm.value;
    return {
      destinationType: this._destinationTypeFor(),
      server: v.server ?? '',
      database: v.database ?? '',
      authentication: v.auth ?? 'sql-auth',
      username: v.username ?? undefined,
      password: v.password ?? undefined,
      trustServerCertificate: true,
      encrypt: true,
      requireSsl: v.requireSsl ?? false,
    };
  }

  testConnection(onSettled?: (result: { connected: boolean; tables: DestinationTable[] }) => void): void {
    this.probeState.set('testing');
    this.probeError.set(null);
    this.schemaSvc.probe(this.getProbeRequest()).subscribe({
      next: res => {
        if (res.connected) {
          // Tagged 'probed' so a MappingSnapshot can tell these apart from anything the user creates
          // afterwards via "+ Add a table"/"+ Add column" (both tagged 'userCreated').
          const tables = res.tables.map(t => ({
            ...t,
            origin: 'probed' as const,
            columns: t.columns.map(c => ({ ...c, origin: 'probed' as const })),
          }));
          this.sqlTables.set(tables);
          this.probeState.set('ok');
          onSettled?.({ connected: true, tables });
        } else {
          this.probeState.set('error');
          this.probeError.set(res.error ?? 'Connection failed.');
          onSettled?.({ connected: false, tables: [] });
        }
      },
      error: err => {
        this.probeState.set('error');
        this.probeError.set(err?.error?.error ?? err?.message ?? 'Connection failed.');
        onSettled?.({ connected: false, tables: [] });
      },
    });
  }
}
