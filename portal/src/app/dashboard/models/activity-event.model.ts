export type ActivitySeverity = 'info' | 'warning' | 'error' | 'success';

export interface ActivityEvent {
  id:          string;
  message:     string;
  severity:    ActivitySeverity;
  timestamp:   Date;
  pipelineId?: string;
}
