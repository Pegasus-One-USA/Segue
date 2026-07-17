import { Injectable, signal } from '@angular/core';
import { KpiMetric } from '../models/kpi-metric.model';
import { HealthCheck } from '../models/health-check.model';

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

@Injectable({ providedIn: 'root' })
export class DashboardService {
  readonly kpis          = signal<KpiMetric[]>(MOCK_KPIS);
  readonly health        = signal<HealthCheck[]>(MOCK_HEALTH);
  readonly lastRefreshed = signal<Date>(new Date());

  refresh(): void {
    this.lastRefreshed.set(new Date());
    // TODO: replace KPIs/health with real HTTP calls
  }
}
