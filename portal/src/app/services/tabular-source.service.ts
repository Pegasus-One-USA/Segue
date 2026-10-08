import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { TABULAR_SOURCE_ENDPOINTS } from '../core/api-endpoints';

/** TabularSourceFileDto: an uploaded CSV, described without its content. */
export interface TabularSourceFile {
  id: string;
  fileName: string;
  sizeBytes: number;
  rowCount: number;
  columns: string[];
  createdBy: string | null;
  createdOnUtc: string;
}

/** TabularSqlConnectionDto: a saved database. Never carries the connection string. */
export interface TabularSqlConnection {
  engine: string;
  secretKeyVaultName: string;
  secretName: string;
  id: string | null;
  name: string | null;
  createdBy: string | null;
  updatedOnUtc: string | null;
}

export interface TabularSqlConnectionTest {
  ok: boolean;
  message: string;
}

export interface TabularTemplatePreset {
  resourceType: string;
  template: string;
}

export interface TabularPreviewRequest {
  kind: 'csv' | 'sql';
  fileId: string | null;
  sqlEngine: string | null;
  secretKeyVaultName: string | null;
  secretName: string | null;
  query: string | null;
  templates: string;
}

export interface TabularPreview {
  columns: string[];
  rowsRead: number;
  resources: { resourceType: string; resourceId: string | null; rowNumber: number; json: string }[];
  errors: string[];
  missingColumns: string[];
}

/** One resource type a source reads, as the node stores it (tab_streams). */
export interface TabularStreamEntry {
  resourceType: string;
  query?: string | null;
  fileId?: string | null;
  rowFilterColumn?: string | null;
  rowFilterValue?: string | null;
  template: unknown;
}

export interface TabularCheckRequest {
  kind: 'csv' | 'sql';
  sqlEngine: string | null;
  secretKeyVaultName: string | null;
  secretName: string | null;
  /** The entries exactly as the node stores them (JSON text). */
  streams: string;
  preview: boolean;
}

export interface TabularStreamCheck {
  index: number;
  resourceType: string;
  passed: boolean;
  problems: string[];
  columns: string[];
  missingColumns: string[];
  rowsRead: number | null;
  resources: { resourceType: string; resourceId: string | null; rowNumber: number; json: string }[];
  rowErrors: string[];
}

export interface TabularCheckResult {
  allPassed: boolean;
  streams: TabularStreamCheck[];
}

/** CSV / SQL Table source setup (TabularSourcesController). File contents and connection strings go to the API
 *  and are never kept in the workflow: the node stores only file ids and the secret reference. */
@Injectable({ providedIn: 'root' })
export class TabularSourceService {
  private readonly http = inject(HttpClient);

  upload(file: File): Observable<TabularSourceFile> {
    const body = new FormData();
    body.append('file', file, file.name);
    return this.http.post<TabularSourceFile>(TABULAR_SOURCE_ENDPOINTS.files, body);
  }

  getFile(id: string): Observable<TabularSourceFile> {
    return this.http.get<TabularSourceFile>(TABULAR_SOURCE_ENDPOINTS.file(id));
  }

  listFiles(): Observable<TabularSourceFile[]> {
    return this.http.get<TabularSourceFile[]>(TABULAR_SOURCE_ENDPOINTS.files);
  }

  saveSqlConnection(engine: string, connectionString: string, name?: string): Observable<TabularSqlConnection> {
    return this.http.post<TabularSqlConnection>(TABULAR_SOURCE_ENDPOINTS.sqlConnections, { engine, connectionString, name: name ?? null });
  }

  listSqlConnections(): Observable<TabularSqlConnection[]> {
    return this.http.get<TabularSqlConnection[]>(TABULAR_SOURCE_ENDPOINTS.sqlConnections);
  }

  updateSqlConnection(id: string, change: { name?: string | null; connectionString?: string | null }): Observable<TabularSqlConnection> {
    return this.http.put<TabularSqlConnection>(TABULAR_SOURCE_ENDPOINTS.sqlConnection(id), {
      name: change.name ?? null,
      connectionString: change.connectionString ?? null,
    });
  }

  deleteSqlConnection(id: string): Observable<void> {
    return this.http.delete<void>(TABULAR_SOURCE_ENDPOINTS.sqlConnection(id));
  }

  testSqlConnection(id: string): Observable<TabularSqlConnectionTest> {
    return this.http.post<TabularSqlConnectionTest>(TABULAR_SOURCE_ENDPOINTS.sqlConnectionTest(id), {});
  }

  templatePresets(): Observable<TabularTemplatePreset[]> {
    return this.http.get<TabularTemplatePreset[]>(TABULAR_SOURCE_ENDPOINTS.templatePresets);
  }

  preview(request: TabularPreviewRequest): Observable<TabularPreview> {
    return this.http.post<TabularPreview>(TABULAR_SOURCE_ENDPOINTS.preview, request);
  }

  /** Checks every resource type's query or file against what it reads; with `preview`, also builds its first rows. */
  check(request: TabularCheckRequest): Observable<TabularCheckResult> {
    return this.http.post<TabularCheckResult>(TABULAR_SOURCE_ENDPOINTS.check, request);
  }
}
