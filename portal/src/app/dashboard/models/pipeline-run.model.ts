import { ExecutionStatus, RouteExecution } from '../../execution-history/models/execution-history.model';

export type PipelineRunStatus =
  | 'running'
  | 'completed'
  | 'completedWithErrors'
  | 'failed'
  | 'skipped'
  | 'queued'
  | 'cancelled';

export interface PipelineRun {
  id:           string;
  name:         string;
  sourceType:   string;
  status:       PipelineRunStatus;
  startedAt:    Date;
  duration?:    number;
  triggeredBy?: string;
  recordCount?: number;
}

// Runtime Plane workflow-run status (WorkflowRunStatus enum) → the table's badge vocabulary.
const STATUS_MAP: Record<ExecutionStatus, PipelineRunStatus> = {
  Pending:            'queued',
  Running:            'running',
  Succeeded:          'completed',
  Failed:             'failed',
  Cancelled:          'cancelled',
  PartialSuccess:     'completedWithErrors',
  AwaitingBulkExport: 'running',
};

/**
 * Maps a Runtime Plane workflow run (the grain the Execution History screen shows, and the path
 * "Run" in the Workflow Builder actually takes) onto the dashboard's Recent Pipelines row.
 */
export function mapRouteExecution(dto: RouteExecution): PipelineRun {
  return {
    id:          dto.id,
    name:        dto.pipelineName,
    sourceType:  dto.sourceSystemType || dto.sourceName || '—',
    status:      STATUS_MAP[dto.status] ?? 'queued',
    startedAt:   new Date(dto.startedAt),
    duration:    dto.durationMs ?? undefined,
    triggeredBy: dto.triggeredBy ?? undefined,
    recordCount: dto.nodeRunCount,
  };
}
