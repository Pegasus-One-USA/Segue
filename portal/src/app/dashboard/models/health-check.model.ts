export type HealthStatus = 'online' | 'degraded' | 'offline';

export interface HealthCheck {
  id:        string;
  label:     string;
  status:    HealthStatus;
  latencyMs?: number;
  detail?:   string;
}
