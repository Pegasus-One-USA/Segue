import { Component, inject, input, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { DestinationSchemaService, DestinationTable, DestinationProbeRequest } from '../../../../services/destination-schema.service';
import { buildConnectionMetadata, buildSqlConnectionString } from '../../../../destination-connections/utils/destination-connection-secret.util';
import { WizardDestinationFormApi } from './destination-form-api';

export type SqlFamilyEngine = 'sqlserver' | 'mysql' | 'postgres' | 'azuresql';

/**
 * Shared engine behind SqlServer/AzureSql/MySql/PostgreSql's connection forms — the server/database/schema/
 * auth fields + destination-schema.service.ts probing logic that used to be duplicated between
 * DestinationWizardComponent's inline sqlForm (Step 1) and DestinationConnectionFormComponent's own copy.
 * `engine` is a fixed input (set by whichever thin wrapper — SqlServerDestinationFormComponent etc. — loads
 * this), not a dropdown inside the form: which family of SQL destination this is is now decided by which
 * wrapper the registry resolved to, not a choice made inside the form itself.
 */
@Component({
  selector: 'app-sql-family-destination-form',
  standalone: true,
  imports: [ReactiveFormsModule],
  templateUrl: './sql-family-destination-form.component.html',
  styleUrls: ['../destination-wizard.component.scss', './sql-family-destination-form.component.scss'],
})
export class SqlFamilyDestinationFormComponent implements WizardDestinationFormApi {
  private readonly fb = inject(FormBuilder);
  private readonly schemaSvc = inject(DestinationSchemaService);

  readonly engine = input.required<SqlFamilyEngine>();

  /** Password has no required validator (optional by default) — SQL never hard-blocks reopening a saved
   *  connection with a blank password. This is only consulted by getMetadata() below, to avoid rebuilding
   *  a connection string with a blank embedded password on a no-op re-save. */
  readonly reusingExisting = input<boolean>(false);
  /** Set alongside reusingExisting — when present and the password field is blank, testConnection() calls
   *  GetSchema to resolve the stored secret server-side instead of probing with a blank password. */
  readonly existingDestinationId = input<string | null>(null);

  /** AzureSql has no live UI anywhere prior to this refactor — it reuses SqlServer's exact fields/behavior,
   *  differing only in the DestinationType/dest_engine tag it's saved under (see _destinationTypeFor below). */
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

  constructor() {
    // A successful Test Connection leaves probeState() 'ok' and sqlTables() holding THAT database's tables —
    // nothing previously invalidated either when server/database/auth/username/password/requireSsl was then
    // edited (e.g. switching to a different database) without re-testing. That let a stale 'ok' survive the
    // edit: DestinationWizardComponent.next() (see its own remarks) treats probeState() === 'ok' as "already
    // verified, no need to re-test" and copies whatever sqlTables() currently holds straight through — which,
    // uninvalidated, would be the PREVIOUS database's table list, not the newly-typed one's. Resetting here
    // (idle is already an untested no-op, so only 'ok'/'error' ever actually reset) forces a real re-test
    // before Next can trust this form's probe result again.
    this.sqlForm.valueChanges.subscribe(() => {
      if (this.probeState() !== 'idle') this.resetProbe();
    });
  }

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
    // buildSqlConnectionString bakes the password into the connection string even when it's blank
    // (e.g. "...;User Id=x;Password=;..."), which is non-empty and would overwrite a working stored secret
    // with a broken one on a no-op re-save — same guard as
    // WorkflowBuildAssemblerService.buildDestinationRequest's isSql branch.
    const secret =
      this.reusingExisting() && !config['dest_password'] ? null : buildSqlConnectionString(config);
    return {
      fields: JSON.parse(buildConnectionMetadata(config, 'sql')) as Record<string, string>,
      secret,
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
    // Reusing an existing connection with a blank (never-re-displayed) password can't be probed ad-hoc — a
    // blank password would just fail auth against the real server. GetSchema resolves the saved destination's
    // real stored secret server-side instead (same as MongoDestinationConnectionTestService's destinationId
    // path), which doubles as a genuine connectivity check: an unreachable server or a rotated/invalid
    // credential surfaces as an HTTP error here exactly like a failed probe() would.
    const id = this.existingDestinationId();
    const willUseStoredSecret = this.reusingExisting() && !!id && !this.sqlForm.value.password;
    console.info(
      `[SQL-family Test Connection] engine=${this.engine()} reusingExisting=${this.reusingExisting()} ` +
      `existingDestinationId=${id ?? '(none)'} passwordTyped=${!!this.sqlForm.value.password} ` +
      `=> ${willUseStoredSecret ? 'resolving stored secret server-side via GetSchema' : 'probing with the form\'s own (typed) password'}`,
    );
    if (willUseStoredSecret) {
      this.schemaSvc.getSchema(id!).subscribe({
        next: res => {
          console.info(`[SQL-family Test Connection] GetSchema succeeded for destination id=${id} — ${res.tables.length} table(s) returned`);
          const tables = res.tables.map(t => ({
            ...t,
            origin: 'probed' as const,
            columns: t.columns.map(c => ({ ...c, origin: 'probed' as const })),
          }));
          this.sqlTables.set(tables);
          this.probeState.set('ok');
          onSettled?.({ connected: true, tables });
        },
        error: err => {
          console.error(`[SQL-family Test Connection] GetSchema FAILED for destination id=${id}`, err);
          this.probeState.set('error');
          this.probeError.set(err?.error?.error ?? err?.message ?? 'Connection failed.');
          onSettled?.({ connected: false, tables: [] });
        },
      });
      return;
    }
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
