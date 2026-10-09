import { Injectable, inject } from '@angular/core';
import { Observable, catchError, forkJoin, map, of } from 'rxjs';
import { EhrWriteCapabilitiesService } from '../../../../services/ehr-write-capabilities.service';
import { ISourceConnectionService } from '../../../../source-connections/services/i-source-connection.service';
import { EhrWriteTarget, ehrWriteOptInsOf } from './ehr-write-type-grid.model';
import { EhrWriteVendor, runModeOf, savedWriteVendorOf } from '../destination-forms/ehr-write-back/ehr-write-back.model';

/**
 * Loads what the EHR write-back grid needs about a destination's target: what the EHR accepts, whether the target
 * connection has the vendor's write APIs activated, and the destination's Step 1 opt-ins. A test-as run writes to a
 * test server, where nothing waits for activation. A failed connection lookup counts as not activated.
 */
@Injectable({ providedIn: 'root' })
export class EhrWriteTargetService {
  private readonly capabilitiesSvc = inject(EhrWriteCapabilitiesService);
  private readonly sourceConnectionSvc = inject(ISourceConnectionService);

  /** From the destination's fields (getFullConfig on Step 1's Next, or the saved node on reopen). The vendor is the
   *  tile's when given, else the one the fields write to (savedWriteVendorOf). Only a run that really is a test run
   *  (runModeOf) skips the activation check; a saved test run that was also a dry run now reopens as a plain dry run of
   *  the vendor, so its connection's own flag counts. */
  load(fields: Record<string, string>, vendor: EhrWriteVendor | null = null): Observable<EhrWriteTarget> {
    vendor = vendor ?? savedWriteVendorOf(fields);
    const connectionId = fields['dest_sourceConnectionId'] || null;
    const activated$: Observable<boolean> = runModeOf(fields).mode === 'test'
      ? of(true)
      : connectionId
        ? this.sourceConnectionSvc.getById(connectionId).pipe(
            map((connection) => connection?.vendorWriteApisActivated === true),
            catchError(() => of(false)),
          )
        : of(false);
    return forkJoin({ result: this.capabilitiesSvc.forVendor(vendor), activated: activated$ }).pipe(
      map(({ result, activated }) => ({
        vendor,
        capabilities: result.capabilities,
        vendorWriteApisActivated: activated,
        optIns: ehrWriteOptInsOf(fields),
      })),
    );
  }
}
