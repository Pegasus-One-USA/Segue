import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { catchError, of, tap } from 'rxjs';
import { PERMISSIONS_ENDPOINTS } from '../core/api-endpoints';
import { NodeCatalogEntry, NodeCatalogEntryDto, mapNodeCatalogEntryDto } from '../models/node-catalog.model';

/**
 * Fetches and caches the canonical Node Catalog (GET /api/v1/permissions/node-catalog) — the single
 * source of truth the Workflow Builder Node Library and Role Permissions' "Workflow Nodes" section
 * both read from. Loaded once per session (both consumers call `ensureLoaded()`; the second call is
 * a no-op while the first is in flight or once it's resolved) rather than once per dialog/page open.
 */
@Injectable({ providedIn: 'root' })
export class NodeCatalogService {
  private readonly http = inject(HttpClient);

  private readonly _entries = signal<NodeCatalogEntry[]>([]);
  private readonly _loaded = signal(false);
  private loadStarted = false;

  /** The current catalog — empty until `ensureLoaded()` resolves. */
  readonly entries = this._entries.asReadonly();

  /** True once the catalog has been fetched at least once (even if the fetch failed). */
  readonly loaded = this._loaded.asReadonly();

  /** Idempotent — safe to call from every consumer's constructor/ngOnInit. */
  ensureLoaded(): void {
    if (this.loadStarted) {
      return;
    }
    this.loadStarted = true;

    this.http.get<NodeCatalogEntryDto[]>(PERMISSIONS_ENDPOINTS.nodeCatalog).pipe(
      tap(dtos => {
        this._entries.set((dtos ?? []).map(mapNodeCatalogEntryDto));
        this._loaded.set(true);
      }),
      catchError(err => {
        // Fails closed: an empty catalog means no source/destination node is visible anywhere,
        // rather than guessing — the same "surface it explicitly" principle the backend's
        // NodeCatalogMetadata completeness check applies at startup.
        this._loaded.set(true);
        console.error('Failed to load the Node Catalog — Node Library and Role Permissions Workflow Nodes will show no nodes.', err);
        return of([] as NodeCatalogEntryDto[]);
      }),
    ).subscribe();
  }

  /** The single entry for a canonical (kind, type) pair, or undefined if none exists. */
  find(kind: 'Source' | 'Destination', type: string): NodeCatalogEntry | undefined {
    return this._entries().find(e => e.kind === kind && e.type === type);
  }
}
