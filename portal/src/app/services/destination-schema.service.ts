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

/**
 * Tests a relational destination connection and loads its tables/columns for the pipeline builder's
 * table/column pickers. Backed by POST /api/v1/destinations/schema-preview (auth token attached by the
 * global auth interceptor).
 */
@Injectable({ providedIn: 'root' })
export class DestinationSchemaService {
  private readonly http = inject(HttpClient);

  probe(request: DestinationProbeRequest): Observable<DestinationSchemaProbe> {
    return this.http.post<DestinationSchemaProbe>(DESTINATION_ENDPOINTS.schemaPreview, request);
  }
}
