import { Injectable } from '@angular/core';
import { Observable, of } from 'rxjs';
import { delay, tap } from 'rxjs/operators';
import { MappingSnapshot, MappingSnapshotSummary } from './mapping-snapshot.model';

const STORAGE_PREFIX = 'fhirbridge.mappingSnapshot.';

/**
 * Mapping-only persistence for the Map Fields screen, independent of the workflow save/load path.
 * Backend is not ready: this mocks the API with localStorage (mirrors BrandingService's pattern) so
 * save -> close -> reopen -> load genuinely round-trips without a server. Swap the bodies of these
 * three methods for real HttpClient calls once a backend contract exists — callers never change.
 */
@Injectable({ providedIn: 'root' })
export class MappingSnapshotService {
  save(draft: Omit<MappingSnapshot, 'id' | 'createdAt' | 'updatedAt'> & { id?: string }): Observable<MappingSnapshot> {
    const now = new Date().toISOString();
    const existing = draft.id ? this.readLocal(draft.id) : null;
    const saved: MappingSnapshot = {
      ...draft,
      id: draft.id ?? this.newId(),
      createdAt: existing?.createdAt ?? now,
      updatedAt: now,
    };

    return of(saved).pipe(
      delay(300),
      tap(v => this.persistLocal(v)),
    );
  }

  get(id: string): Observable<MappingSnapshot | null> {
    return of(this.readLocal(id)).pipe(delay(200));
  }

  list(): Observable<MappingSnapshotSummary[]> {
    const summaries: MappingSnapshotSummary[] = [];
    for (let i = 0; i < localStorage.length; i++) {
      const key = localStorage.key(i);
      if (!key?.startsWith(STORAGE_PREFIX)) continue;
      const snapshot = this.readLocal(key.slice(STORAGE_PREFIX.length));
      if (!snapshot) continue;
      summaries.push({
        id: snapshot.id,
        name: snapshot.name,
        destType: snapshot.destType,
        activeGroup: snapshot.activeGroup,
        updatedAt: snapshot.updatedAt,
        mappingRowCount: snapshot.mappingRows.length,
      });
    }
    summaries.sort((a, b) => b.updatedAt.localeCompare(a.updatedAt));
    return of(summaries).pipe(delay(200));
  }

  private newId(): string {
    return Date.now().toString(36) + Math.random().toString(36).slice(2, 9);
  }

  private persistLocal(snapshot: MappingSnapshot): void {
    try { localStorage.setItem(STORAGE_PREFIX + snapshot.id, JSON.stringify(snapshot)); }
    catch { /* storage unavailable (private mode) — non-fatal */ }
  }

  private readLocal(id: string): MappingSnapshot | null {
    try {
      const raw = localStorage.getItem(STORAGE_PREFIX + id);
      return raw ? JSON.parse(raw) as MappingSnapshot : null;
    } catch {
      return null;
    }
  }
}
