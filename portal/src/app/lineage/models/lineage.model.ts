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
