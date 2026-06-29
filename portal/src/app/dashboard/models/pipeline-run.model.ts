export type PipelineRunStatus = 'running' | 'completed' | 'failed' | 'queued' | 'cancelled';

export interface PipelineRun {
  id:           string;
  name:         string;
  sourceType:   string;
  status:       PipelineRunStatus;
  startedAt:    Date;
  duration?:    number;
  triggeredBy:  'manual' | 'schedule' | 'api';
  recordCount?: number;
}
