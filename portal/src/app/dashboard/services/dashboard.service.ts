import { Injectable, inject, signal } from '@angular/core';
import { KpiMetric } from '../models/kpi-metric.model';
import { HealthCheck } from '../models/health-check.model';
import { ActivityEvent, ActivitySeverity } from '../models/activity-event.model';
import { UserActivityLogsApiService } from '../../activity/services/user-activity-logs-api.service';
import { UserActivityLog } from '../../activity/models/user-activity-log.model';

const MOCK_KPIS: KpiMetric[] = [
  {
    id: 'pipelines', label: 'Total Pipelines', value: 24, icon: '⚡', accent: 'blue',
    trend: { direction: 'up', value: '+3 this week' },
  },
  {
    id: 'running', label: 'Active Executions', value: 7, icon: '▶', accent: 'green',
    trend: { direction: 'flat', value: 'live now' },
  },
  {
    id: 'success', label: 'Success Rate', value: '94.2', unit: '%', icon: '✓', accent: 'green',
    trend: { direction: 'up', value: '+2.1% vs last week' },
  },
  {
    id: 'records', label: 'Records Processed', value: '2.4M', icon: '◈', accent: 'blue',
    trend: { direction: 'up', value: '+18% today' },
  },
];

const MOCK_HEALTH: HealthCheck[] = [
  { id: 'epic-fhir', label: 'Epic FHIR R4',   status: 'online',   latencyMs: 142 },
  { id: 'api-gw',    label: 'API Gateway',     status: 'online',   latencyMs: 28  },
  { id: 'queue',     label: 'Message Queue',   status: 'online',   detail: '12 pending' },
  { id: 'db',        label: 'Database',        status: 'online',   latencyMs: 4   },
  { id: 'auth',      label: 'Auth Service',    status: 'online',   latencyMs: 11  },
  { id: 'storage',   label: 'Storage',         status: 'degraded', detail: '87% capacity' },
];

function toActivityEvent(entry: UserActivityLog): ActivityEvent {
  return {
    id: entry.id,
    message: entry.activity,
    severity: toSeverity(entry),
    timestamp: new Date(entry.occurredOnUtc),
  };
}

// Status (the operation's outcome) takes priority over severity (the log's own importance level) for coloring —
// a "Failed"/"Denied" entry should always read as an error regardless of the severity it was logged at.
function toSeverity(entry: UserActivityLog): ActivitySeverity {
  if (entry.status === 'Failed' || entry.status === 'Denied') return 'error';
  if (entry.status === 'Success') return 'success';
  if (entry.severity === 'Critical') return 'error';
  if (entry.severity === 'Warning') return 'warning';
  return 'info';
}

@Injectable({ providedIn: 'root' })
export class DashboardService {
  private readonly activityApi = inject(UserActivityLogsApiService);

  readonly kpis          = signal<KpiMetric[]>(MOCK_KPIS);
  readonly health        = signal<HealthCheck[]>(MOCK_HEALTH);
  readonly activity      = signal<ActivityEvent[]>([]);
  readonly lastRefreshed = signal<Date>(new Date());

  constructor() {
    this.loadActivity();
  }

  refresh(): void {
    this.lastRefreshed.set(new Date());
    this.loadActivity();
    // TODO: replace KPIs/health with real HTTP calls
  }

  private loadActivity(): void {
    this.activityApi.list({ page: 1, pageSize: 6 }).subscribe({
      next: result => this.activity.set(result.items.map(toActivityEvent)),
      error: () => this.activity.set([]),
    });
  }
}
