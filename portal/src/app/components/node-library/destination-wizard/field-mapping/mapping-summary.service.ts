import { Injectable } from '@angular/core';
import { Observable, of } from 'rxjs';
import { delay, tap } from 'rxjs/operators';
import { MappingSummaryDocument } from './field-mapping-summary.model';

const STORAGE_KEY = 'fhirbridge.mappingSummary.latest';

/**
 * Persistence for the canonical Mapping JSON (see field-mapping-summary.model.ts) — the frontend's only
 * contract with the backend for this screen: accepts a MappingSummaryDocument, returns one back.
 * Backend isn't ready: this mocks the API with localStorage (same pattern as MappingSnapshotService)
 * so save -> close -> reopen -> load genuinely round-trips without a server. Swap the bodies of these
 * two methods for real HttpClient calls once a backend contract exists — callers never change.
 */
@Injectable({ providedIn: 'root' })
export class MappingSummaryService {
  save(doc: MappingSummaryDocument): Observable<MappingSummaryDocument> {
    return of(doc).pipe(
      delay(250),
      tap(v => this.persistLocal(v)),
    );
  }

  load(): Observable<MappingSummaryDocument | null> {
    return of(this.readLocal()).pipe(delay(200));
  }

  private persistLocal(doc: MappingSummaryDocument): void {
    try { localStorage.setItem(STORAGE_KEY, JSON.stringify(doc)); }
    catch { /* storage unavailable (private mode) — non-fatal */ }
  }

  private readLocal(): MappingSummaryDocument | null {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      return raw ? JSON.parse(raw) as MappingSummaryDocument : null;
    } catch {
      return null;
    }
  }
}
