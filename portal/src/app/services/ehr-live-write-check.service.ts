import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of, switchMap } from 'rxjs';
import { WorkflowApiService, WorkflowDefinitionDto, WorkflowRunDto } from './workflow-api.service';
import { runModeOf, savedWriteVendorOf, vendorLabel } from '../components/node-library-v2/destination-wizard/destination-forms/ehr-write-back/ehr-write-back.model';

/** The EHRs a workflow's live EHR Write-Back destinations write into (labels, each once). A test run, a dry run and a
 *  plain FHIR server write-back are not live EHR writes. */
export function liveEhrWritesOf(definition: Pick<WorkflowDefinitionDto, 'nodes'>): string[] {
  const labels: string[] = [];
  for (const node of definition.nodes ?? []) {
    let fields: Record<string, string>;
    try {
      const parsed: unknown = JSON.parse(node.configurationJson || '{}');
      if (!parsed || typeof parsed !== 'object') continue;
      fields = parsed as Record<string, string>;
    } catch {
      continue;
    }
    if (fields['__transformId'] !== 'dest-ehr-writeback' || runModeOf(fields) !== 'live') continue;
    const vendor = savedWriteVendorOf(fields);
    if (!vendor || vendor === 'GenericFhir') continue;
    const label = vendorLabel(vendor);
    if (!labels.includes(label)) labels.push(label);
  }
  return labels;
}

/** A run that finished successfully after the workflow was last changed: the workflow as it is now has run before. */
export function hasCompletedRunSince(runs: readonly WorkflowRunDto[], modifiedOnUtc: string | null | undefined): boolean {
  const since = modifiedOnUtc ? Date.parse(modifiedOnUtc) : Number.NaN;
  return runs.some(run => run.status === 'Succeeded'
    && (Number.isNaN(since) || (!!run.startedAt && Date.parse(run.startedAt) >= since)));
}

/**
 * Before a workflow runs: the EHR to ask about ("Write into <EHR> now?") when it has a live EHR Write-Back destination
 * and has never completed a run as it is now (no successful run since it was last saved, which is when a destination
 * is switched to live); null when there is nothing to ask. A workflow that cannot be read is not asked about (the run
 * itself reports the problem); run history that cannot be read counts as no completed run, so it asks.
 */
@Injectable({ providedIn: 'root' })
export class EhrLiveWriteCheckService {
  private readonly api = inject(WorkflowApiService);

  firstLiveWrite(workflowId: string, modifiedOnUtc: string | null | undefined): Observable<string | null> {
    return this.api.load(workflowId).pipe(
      map(definition => liveEhrWritesOf(definition)),
      catchError(() => of<string[]>([])),
      switchMap(labels => {
        if (labels.length === 0) return of(null);
        const ehr = labels.join(' and ');
        return this.api.runs(workflowId).pipe(
          map(runs => (hasCompletedRunSince(runs ?? [], modifiedOnUtc) ? null : ehr)),
          catchError(() => of(ehr)),
        );
      }),
    );
  }
}
