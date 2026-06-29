import { Injectable, computed, signal } from '@angular/core';
import { PipelineRun, PipelineRunStatus } from '../models/pipeline-run.model';

const MOCK_RUNS: PipelineRun[] = [
  {
    id: 'r1', name: 'Epic Patient Pull', sourceType: 'Epic FHIR R4',
    status: 'completed', startedAt: new Date(Date.now() - 4 * 60000),
    duration: 18400, triggeredBy: 'schedule', recordCount: 12480,
  },
  {
    id: 'r2', name: 'FHIR Export — Consent', sourceType: 'Epic FHIR R4',
    status: 'running', startedAt: new Date(Date.now() - 2 * 60000),
    triggeredBy: 'manual',
  },
  {
    id: 'r3', name: 'Normalize Group Merge', sourceType: 'Cerner FHIR',
    status: 'failed', startedAt: new Date(Date.now() - 12 * 60000),
    duration: 30200, triggeredBy: 'api',
  },
  {
    id: 'r4', name: 'Aggregate Analytics', sourceType: 'Epic FHIR R4',
    status: 'queued', startedAt: new Date(Date.now() - 60000),
    triggeredBy: 'schedule',
  },
  {
    id: 'r5', name: 'SMART Discovery Sync', sourceType: 'Athena Health',
    status: 'completed', startedAt: new Date(Date.now() - 30 * 60000),
    duration: 4200, triggeredBy: 'manual', recordCount: 840,
  },
  {
    id: 'r6', name: 'Consent Validation Run', sourceType: 'Epic FHIR R4',
    status: 'completed', startedAt: new Date(Date.now() - 60 * 60000),
    duration: 9100, triggeredBy: 'schedule', recordCount: 5320,
  },
  {
    id: 'r7', name: 'Lab Results Pipeline', sourceType: 'Epic FHIR R4',
    status: 'cancelled', startedAt: new Date(Date.now() - 90 * 60000),
    duration: 1200, triggeredBy: 'manual',
  },
];

@Injectable({ providedIn: 'root' })
export class PipelineRunService {
  readonly runs = signal<PipelineRun[]>(MOCK_RUNS);

  readonly executionCounts = computed(() => {
    const zero: Record<PipelineRunStatus, number> = {
      running: 0, completed: 0, failed: 0, queued: 0, cancelled: 0,
    };
    return this.runs().reduce((acc, r) => ({ ...acc, [r.status]: acc[r.status] + 1 }), zero);
  });

  fetchRecent(): void {
    // TODO: replace with real HTTP call
  }

  triggerRun(_id: string): void {
    // TODO: replace with real HTTP call
  }
}
