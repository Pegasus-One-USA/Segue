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
  referenceTargetTypes: string[];   // resource types this Reference leaf may point at, e.g. ["Patient"]
}

/**
 * Resolves which reference field on `childFields` must be mapped because the resource is configured
 * as a child of `parentResourceType` (e.g. "subject.reference" for a Patient parent). Mirrors
 * `ParentReferenceResolver` (FHIRBridge.Application/Services) field-for-field — the two must stay in
 * sync, since the backend independently re-validates at save time and this is only the UI's
 * auto-lock/preview of that same decision. Returns null if no reference field on the child can target
 * that parent type at all (an invalid pairing), unless `referenceFieldOverride` names a real field.
 */
export function resolveParentReferenceField(
  childFields: FhirElement[],
  parentResourceType: string,
  referenceFieldOverride?: string | null,
): FhirElement | null {
  if (referenceFieldOverride) {
    return childFields.find(f => f.fhirPath === referenceFieldOverride) ?? null;
  }

  const candidates = childFields.filter(
    f => f.fhirPath.endsWith('.reference') && f.referenceTargetTypes.includes(parentResourceType),
  );

  if (candidates.length <= 1) {
    return candidates[0] ?? null;
  }

  // Multiple fields could satisfy the same parent (e.g. Observation.subject and
  // Observation.performer can both target Patient) - break the tie structurally, without
  // hardcoding any resource or field name: prefer the most specific field (fewest allowed target
  // types), then a singular reference over a repeating one, then alphabetical as a final,
  // deterministic tiebreak.
  return [...candidates].sort((a, b) => {
    const byTargetCount = a.referenceTargetTypes.length - b.referenceTargetTypes.length;
    if (byTargetCount !== 0) return byTargetCount;
    const bySingular = (a.cardinality === '0..1' ? 0 : 1) - (b.cardinality === '0..1' ? 0 : 1);
    if (bySingular !== 0) return bySingular;
    return a.fhirPath.localeCompare(b.fhirPath);
  })[0];
}

/**
 * Fetches + caches the FHIR element catalog per resource type. Responses are cached for the app
 * session (the catalog is static reference data). A fetch failure resolves to an empty list so the
 * wizard can fall back to its built-in field set rather than break.
 */
/**
 * Pipeline/runtime "system value" fields, resolved by the mapping engine from the run context rather than the
 * source FHIR document (their jsonPath is a reserved `@token`, not a `$` JSONPath). Offered for every resource so
 * audit/lineage columns (run id, write time, resource type, source id) can be mapped like any other field.
 */
export const SYSTEM_MAPPING_FIELDS: readonly FhirElement[] = [
  { label: 'System › Pipeline Run Id', jsonPath: '@runId', fhirPath: '@runId', cardinality: '0..1', valueType: 'String', isArray: false, arrays: [], referenceTargetTypes: [] },
  { label: 'System › Written At (UTC)', jsonPath: '@now', fhirPath: '@now', cardinality: '0..1', valueType: 'DateTime', isArray: false, arrays: [], referenceTargetTypes: [] },
  { label: 'System › Resource Type', jsonPath: '@resourceType', fhirPath: '@resourceType', cardinality: '0..1', valueType: 'String', isArray: false, arrays: [], referenceTargetTypes: [] },
  { label: 'System › Source Resource Id', jsonPath: '@sourceResourceId', fhirPath: '@sourceResourceId', cardinality: '0..1', valueType: 'String', isArray: false, arrays: [], referenceTargetTypes: [] },
];

@Injectable({ providedIn: 'root' })
export class MappingCatalogService {
  private readonly http = inject(HttpClient);
  private readonly cache = new Map<string, Observable<FhirElement[]>>();

  /** sourceConnectionId lets the backend prefer that source's vendor-specific catalog (Epic, ...) —
   *  omit it (or pass null) to get the generic base-FHIR-R4 catalog, same as before this param existed.
   *  Cache key includes it so switching sources (or generic vs. vendor) never returns another's stale
   *  result for the same resource type. */
  fields(resourceType: string, sourceConnectionId?: string | null): Observable<FhirElement[]> {
    if (!resourceType) return of([]);
    const key = `${resourceType}::${sourceConnectionId ?? ''}`;
    let stream = this.cache.get(key);
    if (!stream) {
      stream = this.http.get<FhirElement[]>(MAPPING_ENDPOINTS.resourceFields(resourceType, sourceConnectionId)).pipe(
        // Prepend the resource-agnostic system-value fields so they're selectable alongside the FHIR elements.
        map(fields => [...SYSTEM_MAPPING_FIELDS, ...fields]),
        catchError(() => of<FhirElement[]>([...SYSTEM_MAPPING_FIELDS])),
        shareReplay(1),
      );
      this.cache.set(key, stream);
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
