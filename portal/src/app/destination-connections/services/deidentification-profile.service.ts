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

/** How many rules were deleted — or, when usedElsewhere is not empty, why none were. */
export interface WorkflowDeIdentificationRuleDeletionResult {
  deleted: number;
  usedElsewhere: string[];
}

/**
 * List/create for de-identification profiles — the destination wizard's "De-identification profile"
 * dropdown and the Transformation Rules screen's profile picker/creator both use this. Deliberately no
 * update/delete of a profile itself: profiles are edited by adding/removing their pre-mapping
 * TransformationRule rows on the Transformation Rules screen. The one bulk delete is deleteWorkflowRules.
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

  /** Deletes every rule of the policy this workflow owns, when its last De-identification step is removed. The
   *  server finds that policy itself (by the workflow id it is named with) and refuses with 409 — deleting
   *  nothing — when anything outside the workflow still uses it. */
  deleteWorkflowRules(workflowId: string): Observable<WorkflowDeIdentificationRuleDeletionResult> {
    return this.http.delete<WorkflowDeIdentificationRuleDeletionResult>(
      DEIDENTIFICATION_ENDPOINTS.deleteWorkflowRules(workflowId));
  }

  preview(profileId: string, resourceType: string, sampleJson: string): Observable<DeIdentificationPreviewResult> {
    return this.http.post<DeIdentificationPreviewResult>(
      DEIDENTIFICATION_ENDPOINTS.preview(profileId), { resourceType, sampleJson });
  }
}
