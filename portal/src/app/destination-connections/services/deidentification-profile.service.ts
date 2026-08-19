import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { DEIDENTIFICATION_ENDPOINTS } from '../../core/api-endpoints';
import {
  CreateDeIdentificationProfileRequest,
  DeIdentificationProfileDto,
} from '../models/destination-configuration.model';

export interface DeIdentificationPreviewResult {
  redactedJson: string;
}

/**
 * List/create for de-identification profiles — the destination wizard's "De-identification profile"
 * dropdown and the Transformation Rules screen's profile picker/creator both use this. Deliberately no
 * update/delete: profiles are edited by adding/removing their pre-mapping TransformationRule rows on the
 * Transformation Rules screen, not here.
 */
@Injectable({ providedIn: 'root' })
export class DeIdentificationProfileService {
  private readonly http = inject(HttpClient);

  list(): Observable<DeIdentificationProfileDto[]> {
    return this.http.get<DeIdentificationProfileDto[]>(DEIDENTIFICATION_ENDPOINTS.list);
  }

  create(request: CreateDeIdentificationProfileRequest): Observable<DeIdentificationProfileDto> {
    return this.http.post<DeIdentificationProfileDto>(DEIDENTIFICATION_ENDPOINTS.create, request);
  }

  preview(profileId: string, resourceType: string, sampleJson: string): Observable<DeIdentificationPreviewResult> {
    return this.http.post<DeIdentificationPreviewResult>(
      DEIDENTIFICATION_ENDPOINTS.preview(profileId), { resourceType, sampleJson });
  }
}
