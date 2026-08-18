import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { GovernanceApiService } from '../../services/governance-api.service';
import { CorrelationSearchResult } from '../../models/correlation-search.model';
import { ErrorLogEntry } from '../../../operations/models/operations.model';

/** Hides per-record failure rows (Severity "Error") whenever a "PartialSuccess" Informational summary is also
 *  present in this same correlationId-scoped result — that summary already describes the batch failure, so the
 *  individual "An unexpected error occurred…" rows underneath it are redundant here. Each keeps its own unique
 *  ErrorReferenceId (ErrorLogs.ErrorReferenceId is uniquely indexed, so it can't be shared across rows), but
 *  they're still reachable in full via the summary's Reference ID link, which navigates by correlationId. */
function visibleErrors(errors: ErrorLogEntry[]): ErrorLogEntry[] {
  const hasSummary = errors.some(x => x.exceptionType === 'PartialSuccess');
  return hasSummary ? errors.filter(x => x.exceptionType === 'PartialSuccess' || x.severity !== 'Error') : errors;
}

export interface TimelineEntry {
  timeUtc: string;
  category: string;
  summary: string;
  raw: unknown;
}

/** Flattens every category array in a CorrelationSearchResult into one chronological, step-typed list —
 *  pure client-side composition over data the endpoint already returns, no new backend call. */
function buildTimeline(result: CorrelationSearchResult): TimelineEntry[] {
  const entries: TimelineEntry[] = [];

  if (result.pipelineRun) {
    const run = result.pipelineRun;
    entries.push({ timeUtc: run.startedOnUtc, category: 'Pipeline Run Started', summary: `Triggered by ${run.triggeredBy ?? '—'} (${run.triggerType ?? '—'})`, raw: run });
    if (run.completedOnUtc) {
      entries.push({ timeUtc: run.completedOnUtc, category: 'Pipeline Run Completed', summary: `Status: ${run.status}`, raw: run });
    }
  }

  for (const x of result.workflowRuns) {
    entries.push({ timeUtc: x.startedAt, category: 'Workflow Run Started', summary: `Triggered by ${x.triggeredBy ?? '—'} (${x.triggerType ?? '—'})`, raw: x });
    if (x.completedAt) {
      entries.push({ timeUtc: x.completedAt, category: 'Workflow Run Completed', summary: `Status: ${x.status}`, raw: x });
    }
  }

  for (const x of result.authenticationLogs) {
    entries.push({ timeUtc: x.occurredOnUtc, category: 'Authentication', summary: `${x.authenticationType} — ${x.success ? 'OK' : 'Failed: ' + (x.failureReason ?? '')}`, raw: x });
  }
  for (const x of result.authorizationLogs) {
    entries.push({ timeUtc: x.occurredOnUtc, category: 'Authorization', summary: `${x.requestPath} — ${x.result} (${x.permissionCode})`, raw: x });
  }
  for (const x of result.dataAccessLogs) {
    entries.push({ timeUtc: x.occurredOnUtc, category: 'Data Access', summary: `${x.resourceType}/${x.resourceId ?? '—'} — ${x.action}`, raw: x });
  }
  for (const x of result.schedulerHistory) {
    entries.push({ timeUtc: x.runTimeUtc, category: 'Scheduler', summary: `${x.schedulerId} — ${x.status} (${x.routeCount} route(s))`, raw: x });
  }
  for (const x of result.retryHistory) {
    entries.push({ timeUtc: x.occurredOnUtc, category: `Retry #${x.retryNumber}`, summary: x.reason, raw: x });
  }
  for (const x of result.exports) {
    entries.push({ timeUtc: x.occurredOnUtc, category: 'Export', summary: `${x.destinationName} — ${x.rowCount} rows, ${x.status}`, raw: x });
  }
  for (const x of result.notifications) {
    entries.push({ timeUtc: x.occurredOnUtc, category: 'Notification', summary: `${x.notificationType} to ${x.recipient} — ${x.status}`, raw: x });
  }
  for (const x of result.errors) {
    const trace = x.traceId ? ` [trace ${x.traceId}]` : '';
    entries.push({ timeUtc: x.occurredOnUtc, category: 'Error', summary: `${x.severity} — ${x.exceptionType}: ${x.message}${trace}`, raw: x });
  }
  for (const x of result.validationFailures) {
    entries.push({ timeUtc: x.occurredOnUtc, category: 'Validation Failure', summary: x.resourceType, raw: x });
  }
  for (const x of result.apiRequests) {
    entries.push({ timeUtc: x.occurredOnUtc, category: 'API Request', summary: `${x.method} ${x.url} — ${x.statusCode ?? '—'} (${x.durationMs}ms)`, raw: x });
  }
  for (const x of result.auditLogs) {
    entries.push({ timeUtc: x.occurredOnUtc, category: 'Audit', summary: `${x.module} — ${x.action} (${x.status})`, raw: x });
  }
  for (const x of result.securityEvents) {
    entries.push({ timeUtc: x.occurredOnUtc, category: 'Security Event', summary: `${x.severity} — ${x.eventType}`, raw: x });
  }
  for (const x of result.smartLaunchLogs) {
    entries.push({ timeUtc: x.occurredOnUtc, category: 'SMART Launch', summary: `${x.sourceName} (${x.launchType}) — ${x.success ? 'OK' : 'Failed: ' + (x.failureReason ?? '')}`, raw: x });
  }

  return entries.sort((a, b) => new Date(a.timeUtc).getTime() - new Date(b.timeUtc).getTime());
}

@Component({
  selector: 'app-correlation-search',
  standalone: true,
  imports: [CommonModule, DatePipe, RouterLink],
  templateUrl: './correlation-search.component.html',
  styleUrl: './correlation-search.component.scss',
})
export class CorrelationSearchComponent implements OnInit {
  private readonly api = inject(GovernanceApiService);
  private readonly route = inject(ActivatedRoute);

  readonly correlationId = signal('');
  readonly loading = signal(false);
  readonly searched = signal(false);
  readonly errorMessage = signal<string | null>(null);
  readonly result = signal<CorrelationSearchResult | null>(null);

  readonly viewMode = signal<'sections' | 'timeline'>('sections');
  readonly expandedIndex = signal<number | null>(null);

  readonly timeline = computed<TimelineEntry[]>(() => {
    const r = this.result();
    return r ? buildTimeline({ ...r, errors: visibleErrors(r.errors) }) : [];
  });

  readonly visibleErrorRows = computed<ErrorLogEntry[]>(() => {
    const r = this.result();
    return r ? visibleErrors(r.errors) : [];
  });

  setViewMode(mode: 'sections' | 'timeline'): void {
    this.viewMode.set(mode);
  }

  toggleRaw(index: number): void {
    this.expandedIndex.set(this.expandedIndex() === index ? null : index);
  }

  rawJson(entry: TimelineEntry): string {
    return JSON.stringify(entry.raw, null, 2);
  }

  ngOnInit(): void {
    const fromQuery = this.route.snapshot.queryParamMap.get('correlationId');
    if (fromQuery) {
      this.correlationId.set(fromQuery);
      this.search();
    }
  }

  onCorrelationIdChange(value: string): void {
    this.correlationId.set(value);
  }

  search(): void {
    if (!this.correlationId().trim()) {
      return;
    }

    this.loading.set(true);
    this.searched.set(true);
    this.errorMessage.set(null);

    this.api.correlationSearch(this.correlationId().trim()).subscribe({
      next: result => {
        this.result.set(result);
        this.loading.set(false);
      },
      error: () => {
        this.result.set(null);
        this.loading.set(false);
        this.errorMessage.set('Search failed. Check the Correlation ID and try again.');
      },
    });
  }
}
