import { Injectable, inject } from '@angular/core';
import { Observable, catchError, forkJoin, map, of } from 'rxjs';
import { EhrWriteCapabilitiesService } from '../../../../services/ehr-write-capabilities.service';
import { ISourceConnectionService } from '../../../../source-connections/services/i-source-connection.service';
import { EhrWriteTarget, ehrWriteOptInsOf } from './ehr-write-type-grid.model';

/**
 * Loads what the EHR write-back grid needs about a destination's target: what the EHR accepts, whether the target
 * connection has the vendor's write APIs activated, and the destination's Step 1 opt-ins. A test-as run writes to a
 * test server, where nothing waits for activation. A failed connection lookup counts as not activated.
 */
@Injectable({ providedIn: 'root' })
export class EhrWriteTargetService {
  private readonly capabilitiesSvc = inject(EhrWriteCapabilitiesService);
  private readonly sourceConnectionSvc = inject(ISourceConnectionService);

  /** From the destination's fields (getFullConfig on Step 1's Next, or the saved node on reopen). */
  load(fields: Record<string, string>): Observable<EhrWriteTarget> {
    const vendor = fields['dest_ehrVendor'] || null;
    const connectionId = fields['dest_sourceConnectionId'] || null;
    const activated$: Observable<boolean> = fields['dest_testAsVendor']
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
