import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { MAPPING_PROFILES_ENDPOINTS } from '../../../../core/api-endpoints';
import { MappingSummaryDocument } from './field-mapping-summary.model';

export interface MappingProfileImportColumnResult {
  table: string;
  column: string;
}

export interface MappingProfileImportResourceResult {
  resourceType: string;
  mappingProfileId: string;
  tablesCreated: string[];
  tablesSkippedAlreadyExisted: string[];
  columnsAdded: MappingProfileImportColumnResult[];
  columnsSkippedAlreadyExisted: MappingProfileImportColumnResult[];
  fieldsInserted: number;
  warnings: string[];
}

export interface MappingProfileImportResult {
  profiles: MappingProfileImportResourceResult[];
}

/**
 * POST /api/v1/mapping-profiles/import — hands the canonical Mapping JSON straight to the backend
 * (MappingImportService.ImportAsync) so a real MappingProfile exists for each mapped resource, and any
 * schema changes it describes (tablesToCreate/columnsToAdd) are applied, as soon as the destination
 * wizard's "Add to Pipeline" step finishes — instead of only after a full workflow build. Safely
 * re-postable: the backend reports already-existing tables/columns/profiles rather than duplicating them.
 * Requires sourceConnectionId/destinationId already be real (see DestinationWizardComponent._save() —
 * only called when both are present).
 */
@Injectable({ providedIn: 'root' })
export class MappingProfileImportService {
  private readonly http = inject(HttpClient);

  import(doc: MappingSummaryDocument): Observable<MappingProfileImportResult> {
    return this.http.post<MappingProfileImportResult>(MAPPING_PROFILES_ENDPOINTS.import, doc);
  }
}
