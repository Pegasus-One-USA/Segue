import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, map, of, shareReplay } from 'rxjs';
import { EHR_WRITE_CAPABILITIES_ENDPOINTS } from '../core/api-endpoints';

/** One resource type an EHR vendor accepts writes for (EhrWriteCapabilityDto). */
export interface EhrWriteCapability {
  resourceType: string;
  operations: string[];
  vendorApiId: string;
  variant: string | null;
  requiresEncounter: boolean;
  optInOnly: boolean;
  allowedApplicationTypes: string[];
  /** The product can send this type live; false keeps it dry-run-only whatever is configured. */
  liveWriteSupported: boolean;
  /** An administrator released it (system setting EhrWriteBack:LiveWriteTypes): a non-dry run sends it. */
  liveReleased: boolean;
}

/** EhrWriteCapabilitiesDto. */
export interface EhrWriteCapabilities {
  vendor: string | null;
  supportsPatientMatch: boolean;
  cloneModeEnabled: boolean;
  capabilities: EhrWriteCapability[];
}

const NONE: EhrWriteCapabilities = { vendor: null, supportsPatientMatch: false, cloneModeEnabled: false, capabilities: [] };

/**
 * Which resource types an EHR vendor accepts writes for. Deny-by-default, the opposite of MappingCatalogService's
 * resource list: a failed call or an unknown vendor means "cannot be written to", never "no filter".
 */
@Injectable({ providedIn: 'root' })
export class EhrWriteCapabilitiesService {
  private readonly http = inject(HttpClient);
  private readonly cache = new Map<string, Observable<EhrWriteCapabilities>>();

  forVendor(vendor: string | null | undefined): Observable<EhrWriteCapabilities> {
    if (!vendor) return of(NONE);
    let stream = this.cache.get(vendor);
    if (!stream) {
      stream = this.http.get<EhrWriteCapabilities>(EHR_WRITE_CAPABILITIES_ENDPOINTS.byVendor(vendor)).pipe(
        map(result => result ?? NONE),
        // Not cached: a transient failure must not leave writes disabled until the page is reloaded.
        catchError(() => {
          this.cache.delete(vendor);
          return of(NONE);
        }),
        shareReplay(1),
      );
      this.cache.set(vendor, stream);
    }

    return stream;
  }

  /** Resource types the vendor can be written to, in the order the API lists them. */
  writableResourceTypes(vendor: string | null | undefined): Observable<string[]> {
    return this.forVendor(vendor).pipe(map(result => result.capabilities.map(c => c.resourceType)));
  }
}
