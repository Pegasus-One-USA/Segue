/** Matches the backend's ResourceLineageRecord (one PHI-free chain-of-custody step). */
export interface LineageEntry {
  pipelineRunId: string | null;
  routeId: string | null;
  sourceConnectionId: string | null;
  destinationId: string | null;
  mappingProfileId: string | null;
  resourceType: string;
  sourceResourceId: string | null;
  action: string;
  status: string;
  occurredOnUtc: string;
}

/** Matches the backend's ResourceLineageChain — the ordered chain of steps for one resource. */
export interface LineageChain {
  sourceResourceId: string | null;
  steps: LineageEntry[];
}

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface LineageFilter {
  pipelineRunId?: string;
  resourceType?: string;
  action?: string;
  status?: string;
  page: number;
  pageSize: number;
}

export interface LineageChainQuery {
  pipelineRunId?: string | null;
  resourceType?: string;
  sourceResourceId?: string | null;
}

/**
 * Matches the backend's FieldLineageRecord — for one resource, one mapped field's source FHIR path resolved to one
 * destination column. Empty unless FieldLineage:Enabled was on when the resource was mapped. PHI-free: carries the
 * field path and transformation, never the resolved value.
 */
export interface FieldLineageEntry {
  pipelineRunId: string;
  mappingProfileId: string | null;
  resourceType: string;
  sourceResourceId: string | null;
  sourceFieldPath: string;
  transformationType: string;
  destinationObject: string | null;
  destinationColumn: string;
  occurredOnUtc: string;
}

export interface FieldLineageQuery {
  resourceType: string;
  sourceResourceId: string;
  pipelineRunId?: string | null;
}
