import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
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

export interface SectionInfo {
  id: string;
  label: string;
  count: number;
}

/** Section ids/labels line up with the `<section [id]>` blocks in the template so the jump-nav
 *  and collapse-all controls can target them without duplicating this list in two places. */
const SECTION_DEFS: { id: string; label: string; count: (r: CorrelationSearchResult, visibleErrors: number) => number }[] = [
  { id: 'sec-pipelineRun', label: 'Pipeline Run', count: r => (r.pipelineRun ? 1 : 0) },
  { id: 'sec-workflowRuns', label: 'Workflow Runs', count: r => r.workflowRuns.length },
  { id: 'sec-auditLogs', label: 'Audit Logs', count: r => r.auditLogs.length },
  { id: 'sec-dataAccessLogs', label: 'Data Access Logs', count: r => r.dataAccessLogs.length },
  { id: 'sec-authenticationLogs', label: 'Authentication Logs', count: r => r.authenticationLogs.length },
  { id: 'sec-securityEvents', label: 'Security Events', count: r => r.securityEvents.length },
  { id: 'sec-authorizationLogs', label: 'Authorization Logs', count: r => r.authorizationLogs.length },
  { id: 'sec-schedulerHistory', label: 'Scheduler History', count: r => r.schedulerHistory.length },
  { id: 'sec-retryHistory', label: 'Retry History', count: r => r.retryHistory.length },
  { id: 'sec-errors', label: 'Errors', count: (_r, visibleErrors) => visibleErrors },
  { id: 'sec-apiRequests', label: 'API Requests', count: r => r.apiRequests.length },
  { id: 'sec-exports', label: 'Exports', count: r => r.exports.length },
  { id: 'sec-notifications', label: 'Notifications', count: r => r.notifications.length },
  { id: 'sec-validationFailures', label: 'Validation Failures', count: r => r.validationFailures.length },
  { id: 'sec-smartLaunchLogs', label: 'SMART Launch Logs', count: r => r.smartLaunchLogs.length },
];

/** Sections with more rows than this are collapsed by default so the page opens as a scannable
 *  overview instead of a wall of tables — the user still expands the ones they care about. */
const AUTO_COLLAPSE_THRESHOLD = 10;

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
  imports: [CommonModule, DatePipe, RouterLink, MatIconModule],
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
  readonly collapsedSections = signal<Set<string>>(new Set());

  readonly timeline = computed<TimelineEntry[]>(() => {
    const r = this.result();
    return r ? buildTimeline({ ...r, errors: visibleErrors(r.errors) }) : [];
  });

  readonly visibleErrorRows = computed<ErrorLogEntry[]>(() => {
    const r = this.result();
    return r ? visibleErrors(r.errors) : [];
  });

  readonly sections = computed<SectionInfo[]>(() => {
    const r = this.result();
    if (!r) {
      return [];
    }
    const visibleErrorCount = this.visibleErrorRows().length;
    return SECTION_DEFS
      .map(def => ({ id: def.id, label: def.label, count: def.count(r, visibleErrorCount) }))
      .filter(s => s.count > 0);
  });

  setViewMode(mode: 'sections' | 'timeline'): void {
    this.viewMode.set(mode);
  }

  isSectionCollapsed(id: string): boolean {
    return this.collapsedSections().has(id);
  }

  toggleSection(id: string): void {
    const next = new Set(this.collapsedSections());
    if (next.has(id)) {
      next.delete(id);
    } else {
      next.add(id);
    }
    this.collapsedSections.set(next);
  }

  toggleAllSections(): void {
    const allCollapsed = this.allSectionsCollapsed();
    this.collapsedSections.set(allCollapsed ? new Set() : new Set(this.sections().map(s => s.id)));
  }

  allSectionsCollapsed(): boolean {
    const collapsed = this.collapsedSections();
    return this.sections().every(s => collapsed.has(s.id));
  }

  jumpToSection(id: string): void {
    if (this.collapsedSections().has(id)) {
      this.toggleSection(id);
    }
    queueMicrotask(() => document.getElementById(id)?.scrollIntoView({ behavior: 'smooth', block: 'start' }));
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

  /** Clears the correlation-id filter and any loaded result — the standard Reset action shared by the
   *  other Logs & Compliance tabs' filter bars. */
  reset(): void {
    this.correlationId.set('');
    this.searched.set(false);
    this.errorMessage.set(null);
    this.result.set(null);
    this.expandedIndex.set(null);
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
        const visibleErrorCount = visibleErrors(result.errors).length;
        const largeSections = SECTION_DEFS.filter(def => def.count(result, visibleErrorCount) > AUTO_COLLAPSE_THRESHOLD);
        this.collapsedSections.set(new Set(largeSections.map(def => def.id)));
      },
      error: () => {
        this.result.set(null);
        this.loading.set(false);
        this.errorMessage.set('Search failed. Check the Correlation ID and try again.');
      },
    });
  }
}
