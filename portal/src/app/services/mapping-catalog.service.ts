import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, of, shareReplay, map } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { MAPPING_ENDPOINTS } from '../core/api-endpoints';

/**
 * One FHIR element from the backend catalog (generated from the Firely R4 model). Carries the
 * array-aware JSONPath and array-ancestor metadata the mapping engine needs, so the wizard never
 * has to guess whether a segment is a collection.
 */
export interface FhirElement {
  label: string;
  jsonPath: string;                 // e.g. "$.name[*].given[*]"
  fhirPath: string;                 // e.g. "name.given"
  cardinality: string;              // "0..1" | "0..*"
  valueType: string;                // String | Integer | Decimal | Boolean | Date | DateTime | Json
  isArray: boolean;
  arrays: string[];                 // array-ancestor fhir paths, e.g. ["name"]
}

/**
 * Fetches + caches the FHIR element catalog per resource type. Responses are cached for the app
 * session (the catalog is static reference data). A fetch failure resolves to an empty list so the
 * wizard can fall back to its built-in field set rather than break.
 */
@Injectable({ providedIn: 'root' })
export class MappingCatalogService {
  private readonly http = inject(HttpClient);
  private readonly cache = new Map<string, Observable<FhirElement[]>>();

  fields(resourceType: string): Observable<FhirElement[]> {
    if (!resourceType) return of([]);
    let stream = this.cache.get(resourceType);
    if (!stream) {
      stream = this.http.get<FhirElement[]>(MAPPING_ENDPOINTS.resourceFields(resourceType)).pipe(
        catchError(() => of<FhirElement[]>([])),
        shareReplay(1),
      );
      this.cache.set(resourceType, stream);
    }
    return stream;
  }

  /** Look up a single element by its FHIR path (resource prefix stripped), e.g. "name.given". */
  fieldByFhirPath(resourceType: string, fhirPath: string): Observable<FhirElement | null> {
    const needle = fhirPath.startsWith(`${resourceType}.`)
      ? fhirPath.slice(resourceType.length + 1)
      : fhirPath;
    return this.fields(resourceType).pipe(
      map(fields => fields.find(f => f.fhirPath === needle) ?? null),
    );
  }
}
