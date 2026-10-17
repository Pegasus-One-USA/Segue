import { Injectable, inject } from '@angular/core';
import { Observable, catchError, forkJoin, map, of, switchMap } from 'rxjs';
import { WorkflowApiService, WorkflowDefinitionDto, WorkflowRunDto } from './workflow-api.service';
import { ISourceConnectionService } from '../source-connections/services/i-source-connection.service';
import { SourceConnectionModel } from '../source-connections/models/source-connection.model';
import { LiveEhrWriteTarget, ehrNamesOf, liveEhrWriteTargetsOfDefinition } from './live-ehr-write-targets.util';

/** What the question names when the workflow cannot be read and its list row names no EHR either. */
export const UNKNOWN_LIVE_EHR = 'the EHR';

/** The EHRs a workflow's live EHR Write-Back destinations write into, by their own names, each once. A test run and
 *  a dry run are not live writes; a plain FHIR server written to for real is. */
export function liveEhrWritesOf(definition: Pick<WorkflowDefinitionDto, 'nodes'>): string[] {
  const names: string[] = [];
  for (const target of liveEhrWriteTargetsOfDefinition(definition.nodes)) {
    const name = ehrNamesOf([target.vendor]);
    if (name && !names.includes(name)) names.push(name);
  }
  return names;
}

/** A run that finished successfully after the workflow was last changed: the workflow as it is now has run before. */
export function hasCompletedRunSince(runs: readonly WorkflowRunDto[], modifiedOnUtc: string | null | undefined): boolean {
  const since = modifiedOnUtc ? Date.parse(modifiedOnUtc) : Number.NaN;
  return runs.some(run => run.status === 'Succeeded'
    && (Number.isNaN(since) || (!!run.startedAt && Date.parse(run.startedAt) >= since)));
}

/** The latest of the given times (ISO strings), or null when none can be read. */
export function latestOf(times: readonly (string | null | undefined)[]): string | null {
  let latest: string | null = null;
  for (const time of times) {
    if (!time || Number.isNaN(Date.parse(time))) continue;
    if (latest === null || Date.parse(time) > Date.parse(latest)) latest = time;
  }
  return latest;
}

/**
 * Before a workflow runs: the EHRs to ask about ("Write into <EHR names> now?") when it has a live EHR Write-Back
 * destination and has never completed a run as it is now; null when there is nothing to ask. "As it is now" means
 * no successful run since the workflow was last saved (which is when a destination is switched to live) or since a
 * connection it writes through was last changed in Settings (ticking "Vendor write APIs activated", or a new base
 * URL). The times come from the definition and connections loaded here, never from a list row that may be stale.
 * Fails safe: a workflow, run history or connection list that cannot be read asks, naming the EHRs the caller already
 * knows (the list row's "Writes to") or {@link UNKNOWN_LIVE_EHR}.
 */
@Injectable({ providedIn: 'root' })
export class EhrLiveWriteCheckService {
  private readonly api = inject(WorkflowApiService);
  private readonly sourceConnections = inject(ISourceConnectionService);

  firstLiveWrite(workflowId: string, knownLiveVendors?: readonly string[] | null): Observable<string | null> {
    return this.api.load(workflowId).pipe(
      map(definition => ({ definition, targets: liveEhrWriteTargetsOfDefinition(definition.nodes) })),
      switchMap(({ definition, targets }) => {
        if (targets.length === 0) return of(null);
        const ehr = ehrNamesOf(targets.map(target => target.vendor));
        return forkJoin({ runs: this.api.runs(workflowId), connections: this.connectionsOf(targets) }).pipe(
          map(({ runs, connections }) => {
            const since = latestOf([
              definition.updatedOnUtc ?? definition.createdOnUtc,
              ...connections.map(connection => connection.modifiedOnUtc ?? connection.createdOnUtc),
            ]);
            return hasCompletedRunSince(runs ?? [], since) ? null : ehr;
          }),
          catchError(() => of(ehr)),
        );
      }),
      catchError(() => of(ehrNamesOf(knownLiveVendors ?? []) || UNKNOWN_LIVE_EHR)),
    );
  }

  /** The connections the live targets write through. None to read when no target names one. */
  private connectionsOf(targets: readonly LiveEhrWriteTarget[]): Observable<SourceConnectionModel[]> {
    const ids = new Set(targets.map(target => target.connectionId.toLowerCase()).filter(id => !!id));
    if (ids.size === 0) return of([]);
    return this.sourceConnections.getAll().pipe(
      map(connections => (connections ?? []).filter(connection => ids.has((connection.id ?? '').toLowerCase()))),
    );
  }
}
