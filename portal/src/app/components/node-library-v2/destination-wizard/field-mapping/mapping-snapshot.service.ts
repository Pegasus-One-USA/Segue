import { Injectable } from '@angular/core';
import { Observable, of } from 'rxjs';
import { delay, tap } from 'rxjs/operators';
import { MappingSnapshot, MappingSnapshotSummary } from './mapping-snapshot.model';

const STORAGE_PREFIX = 'fhirbridge.mappingSnapshot.';
// HIPAA #17: these snapshots can carry mapped field values while a backend contract doesn't exist yet —
// bound how long they linger in the browser rather than persisting indefinitely.
const SNAPSHOT_TTL_MS = 30 * 24 * 60 * 60 * 1000; // 30 days

interface StoredSnapshot {
  snapshot: MappingSnapshot;
  expiresAt: number;
}

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
    const stored: StoredSnapshot = { snapshot, expiresAt: Date.now() + SNAPSHOT_TTL_MS };
    try { localStorage.setItem(STORAGE_PREFIX + snapshot.id, JSON.stringify(stored)); }
    catch { /* storage unavailable (private mode) — non-fatal */ }
  }

  private readLocal(id: string): MappingSnapshot | null {
    try {
      const raw = localStorage.getItem(STORAGE_PREFIX + id);
      if (!raw) return null;

      const stored = JSON.parse(raw) as StoredSnapshot;
      if (!stored.expiresAt || stored.expiresAt < Date.now()) {
        localStorage.removeItem(STORAGE_PREFIX + id);
        return null;
      }

      return stored.snapshot;
    } catch {
      return null;
    }
  }

  /** Called on logout — clears every mapping snapshot for the departing session, expired or not. */
  clearAll(): void {
    const keysToRemove: string[] = [];
    for (let i = 0; i < localStorage.length; i++) {
      const key = localStorage.key(i);
      if (key?.startsWith(STORAGE_PREFIX)) keysToRemove.push(key);
    }
    keysToRemove.forEach(key => localStorage.removeItem(key));
  }
}
