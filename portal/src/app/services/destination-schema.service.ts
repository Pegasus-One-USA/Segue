import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { DESTINATION_ENDPOINTS } from '../core/api-endpoints';

export interface DestinationColumn {
  name: string;
  dataType: string;
  mappingValueType: string;
  isNullable: boolean;
  maxLength: number | null;
  /** Absent (e.g. every real probe() response) is treated as 'probed'. Only the mapping-canvas's own
   *  "+ Add column" flow ever stamps 'userCreated', client-side — the backend never sets this. */
  origin?: 'probed' | 'userCreated';
}

export interface DestinationTable {
  schemaName: string;
  tableName: string;
  fullName: string;
  columns: DestinationColumn[];
  /** Same convention as DestinationColumn.origin — stamped only by the "+ Add a table" flow. */
  origin?: 'probed' | 'userCreated';
}

export interface DestinationSchemaProbe {
  connected: boolean;
  error: string | null;
  tables: DestinationTable[];
}

export interface DestinationProbeRequest {
  destinationType: string;
  server?: string;
  database?: string;
  authentication?: string;
  username?: string;
  password?: string;
  trustServerCertificate?: boolean;
  encrypt?: boolean;
  connectionString?: string;
}

export interface SchemaMutationResult {
  success: boolean;
  error: string | null;
  column: DestinationColumn | null;
  /** Populated by createTable() — the full created table (Id + any requested columns + the FK column, if
   *  any). Also populated by addColumn() when its target table didn't already exist and had to be
   *  auto-created (Id + the one added column) — the caller otherwise has no prior record of that table. */
  table: DestinationTable | null;
}

/** One user-specified column for CreateTableRequest — name + a data type in the same shapes the
 *  add-column modal already offers (fixed keyword, sized string, or decimal(p,s)). */
export interface TableColumnDefinition {
  name: string;
  dataType: string;
}

export interface AddColumnRequest {
  connection: DestinationProbeRequest;
  tableName: string;
  columnName: string;
  dataType: string;
  isNullable?: boolean;
}

export interface CreateTableRequest {
  connection: DestinationProbeRequest;
  tableName: string;
  columns?: TableColumnDefinition[];
  /** Supplying this makes the new table a child: adds a BIGINT FK column referencing parentTable.parentColumn. */
  parentTable?: string;
  /** Defaults server-side to "Id" when parentTable is set. */
  parentColumn?: string;
  /** Defaults server-side to "{parentTableName}Id" when parentTable is set. */
  foreignKeyColumnName?: string;
}

/** Permanently drops a column via a real ALTER TABLE ... DROP COLUMN — irreversible, data included. */
export interface DropColumnRequest {
  connection: DestinationProbeRequest;
  tableName: string;
  columnName: string;
}

/** Changes an existing column's data type via a real ALTER TABLE ... ALTER COLUMN, and optionally
 *  renames it (via sp_rename) when newColumnName differs from columnName. The column's existing
 *  NULL/NOT NULL constraint is always preserved server-side. */
export interface AlterColumnRequest {
  connection: DestinationProbeRequest;
  tableName: string;
  columnName: string;
  newDataType: string;
  newColumnName?: string;
}

export interface SftpConnectionTestRequest {
  host: string;
  port: number;
  username: string;
  password?: string;
  remoteFolder?: string;
}

export interface ConnectionTestResult {
  connected: boolean;
  error: string | null;
}

/**
 * Tests a relational destination connection and loads its tables/columns for the pipeline builder's
 * table/column pickers, executes real schema-authoring DDL (ALTER TABLE / CREATE TABLE) for the mapping
 * canvas's "add column" / "add table" affordances, and tests ad-hoc SFTP connections for CSV
 * destinations. Backed by DestinationSchemaController (auth token attached by the global auth
 * interceptor).
 */
@Injectable({ providedIn: 'root' })
export class DestinationSchemaService {
  private readonly http = inject(HttpClient);

  probe(request: DestinationProbeRequest): Observable<DestinationSchemaProbe> {
    return this.http.post<DestinationSchemaProbe>(DESTINATION_ENDPOINTS.schemaPreview, request);
  }

  /** Tables/columns of an already-saved destination, read server-side via its stored secret reference —
   *  unlike probe(), never needs a plaintext password on the client (which is deliberately never persisted
   *  back onto a workflow node's own config; see workflow-graph-mapper.service.ts's SECRET_FIELD_KEYS). */
  getSchema(destinationId: string): Observable<{ destinationId: string; tables: DestinationTable[] }> {
    return this.http.get<{ destinationId: string; tables: DestinationTable[] }>(
      DESTINATION_ENDPOINTS.schema(destinationId),
    );
  }

  addColumn(request: AddColumnRequest): Observable<SchemaMutationResult> {
    return this.http.post<SchemaMutationResult>(DESTINATION_ENDPOINTS.addColumn, request);
  }

  createTable(request: CreateTableRequest): Observable<SchemaMutationResult> {
    return this.http.post<SchemaMutationResult>(DESTINATION_ENDPOINTS.createTable, request);
  }

  dropColumn(request: DropColumnRequest): Observable<SchemaMutationResult> {
    return this.http.post<SchemaMutationResult>(DESTINATION_ENDPOINTS.dropColumn, request);
  }

  alterColumn(request: AlterColumnRequest): Observable<SchemaMutationResult> {
    return this.http.post<SchemaMutationResult>(DESTINATION_ENDPOINTS.alterColumn, request);
  }

  /** Tests an ad-hoc SFTP connection for a CSV destination (storageType 'sftp'). */
  testSftp(request: SftpConnectionTestRequest): Observable<ConnectionTestResult> {
    return this.http.post<ConnectionTestResult>(DESTINATION_ENDPOINTS.sftpTest, request);
  }
}
