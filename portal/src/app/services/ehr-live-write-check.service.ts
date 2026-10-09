import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of, switchMap } from 'rxjs';
import { WorkflowApiService, WorkflowDefinitionDto, WorkflowRunDto } from './workflow-api.service';
import { ehrNamesOf, liveEhrWriteTargetsOfDefinition } from './live-ehr-write-targets.util';

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

/**
 * Before a workflow runs: the EHRs to ask about ("Write into <EHR names> now?") when it has a live EHR Write-Back
 * destination and has never completed a run as it is now (no successful run since it was last saved, which is when a
 * destination is switched to live); null when there is nothing to ask. A workflow that cannot be read is not asked about (the run
 * itself reports the problem); run history that cannot be read counts as no completed run, so it asks.
 */
@Injectable({ providedIn: 'root' })
export class EhrLiveWriteCheckService {
  private readonly api = inject(WorkflowApiService);

  firstLiveWrite(workflowId: string, modifiedOnUtc: string | null | undefined): Observable<string | null> {
    return this.api.load(workflowId).pipe(
      map(definition => liveEhrWriteTargetsOfDefinition(definition.nodes).map(target => target.vendor)),
      catchError(() => of<string[]>([])),
      switchMap(vendors => {
        if (vendors.length === 0) return of(null);
        const ehr = ehrNamesOf(vendors);
        return this.api.runs(workflowId).pipe(
          map(runs => (hasCompletedRunSince(runs ?? [], modifiedOnUtc) ? null : ehr)),
          catchError(() => of(ehr)),
        );
      }),
    );
  }
}
