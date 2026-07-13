/** Matches the backend's UserActivityAuditLogDto (append-only, hash-chained user activity trail). */
export interface UserActivityLog {
  id: string;
  userId: string | null;
  userEmail: string;
  category: string;
  activity: string;
  status: string;
  entityName: string | null;
  entityId: string | null;
  ipAddress: string | null;
  userAgent: string | null;
  httpMethod: string | null;
  requestPath: string | null;
  details: string | null;
  correlationId: string | null;
  sessionId: string | null;
  failureReason: string | null;
  severity: string;
  occurredOnUtc: string;
}

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface UserActivityLogFilter {
  category?: string;
  status?: string;
  userId?: string;
  search?: string;
  page: number;
  pageSize: number;
}
