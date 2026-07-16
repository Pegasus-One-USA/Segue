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
}

export interface DestinationTable {
  schemaName: string;
  tableName: string;
  fullName: string;
  columns: DestinationColumn[];
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
}

/** Permanently drops a column via a real ALTER TABLE ... DROP COLUMN — irreversible, data included. */
export interface DropColumnRequest {
  connection: DestinationProbeRequest;
  tableName: string;
  columnName: string;
}

/**
 * Tests a relational destination connection and loads its tables/columns for the pipeline builder's
 * table/column pickers, and executes real schema-authoring DDL (ALTER TABLE / CREATE TABLE) for the
 * mapping canvas's "add column" / "add table" affordances. Backed by DestinationSchemaController
 * (auth token attached by the global auth interceptor).
 */
@Injectable({ providedIn: 'root' })
export class DestinationSchemaService {
  private readonly http = inject(HttpClient);

  probe(request: DestinationProbeRequest): Observable<DestinationSchemaProbe> {
    return this.http.post<DestinationSchemaProbe>(DESTINATION_ENDPOINTS.schemaPreview, request);
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
}
