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

export interface TabularSqlConnection {
  engine: string;
  secretKeyVaultName: string;
  secretName: string;
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

/** CSV / SQL Table source setup (TabularSourcesController). File contents and connection strings go to the API
 *  and are never kept in the workflow: the node stores only the file id and the secret reference. */
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

  saveSqlConnection(engine: string, connectionString: string): Observable<TabularSqlConnection> {
    return this.http.post<TabularSqlConnection>(TABULAR_SOURCE_ENDPOINTS.sqlConnections, { engine, connectionString });
  }

  templatePresets(): Observable<TabularTemplatePreset[]> {
    return this.http.get<TabularTemplatePreset[]>(TABULAR_SOURCE_ENDPOINTS.templatePresets);
  }

  preview(request: TabularPreviewRequest): Observable<TabularPreview> {
    return this.http.post<TabularPreview>(TABULAR_SOURCE_ENDPOINTS.preview, request);
  }
}
