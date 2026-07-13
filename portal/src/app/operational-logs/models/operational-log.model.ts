/** Matches the backend's OperationalAuditLogDto (technical/pipeline event log). */
export interface OperationalLog {
  id: string;
  pipelineRunId: string | null;
  resourcePipelineRouteId: string | null;
  sourceConnectionId: string | null;
  destinationId: string | null;
  mappingProfileId: string | null;
  resourceType: string | null;
  action: string;
  status: string;
  message: string;
  resourceCount: number | null;
  triggeredBy: string | null;
  correlationId: string | null;
  occurredOnUtc: string;
}

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface OperationalLogFilter {
  pipelineRunId?: string;
  resourceType?: string;
  action?: string;
  status?: string;
  search?: string;
  page: number;
  pageSize: number;
}
