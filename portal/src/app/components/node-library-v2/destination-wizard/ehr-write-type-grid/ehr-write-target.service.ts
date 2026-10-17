import { Injectable, inject } from '@angular/core';
import { Observable, catchError, forkJoin, map, of } from 'rxjs';
import { EhrWriteCapabilitiesService } from '../../../../services/ehr-write-capabilities.service';
import { ISourceConnectionService } from '../../../../source-connections/services/i-source-connection.service';
import { EhrWriteTarget, ehrWriteOptInsOf } from './ehr-write-type-grid.model';
import { EhrWriteVendor, isTestableVendor, savedWriteVendorOf } from '../destination-forms/ehr-write-back/ehr-write-back.model';

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
   *  tile's when given, else the one the fields write to (savedWriteVendorOf). A run over a FHIR test server (also a dry
   *  run over one) skips the activation check: the test server stands in for an EHR with everything activated. */
  load(fields: Record<string, string>, vendor: EhrWriteVendor | null = null): Observable<EhrWriteTarget> {
    vendor = vendor ?? savedWriteVendorOf(fields);
    const connectionId = fields['dest_sourceConnectionId'] || null;
    const activated$: Observable<boolean> = isTestableVendor(fields['dest_testAsVendor'])
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
