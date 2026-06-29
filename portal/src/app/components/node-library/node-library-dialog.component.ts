import { Component, input, output, inject, computed, signal } from '@angular/core';
import { ModalOverlayComponent } from '../shared/modal-overlay/modal-overlay.component';
import { PipelineStore } from '../../services/pipeline.store';
import { ApplicabilityService } from '../../services/applicability.service';
import { PhaseConfigService } from '../../services/phase-config.service';
import { SOURCES } from '../../data/sources.data';
import { TRANSFORMS } from '../../data/transforms.data';
import { RANK_LABEL } from '../../models/transform.model';
import { CanvasNode } from '../../models/node.model';
import { MergeNodeOption } from '../../models/wizard-state.model';

export type LibraryMode = 'source' | 'transform';

export interface AddTransformEvent {
  attachNode: CanvasNode;
  transformId: string;
  status: string;
}

export interface MergeEvent {
  opt: MergeNodeOption;
}

type ItemStatus = 'enabled' | 'show' | 'caveat' | 'hide' | 'disabled';

interface LibraryItem {
  id: string;
  rank: number;
  name: string;
  sub: string;
  abbr: string;
  color: string;
  category: string | null;
  isSource: boolean;
  status: ItemStatus;
  reason?: string | null;
  group?: string | null;
  isMerge?: boolean;
  mergeOpt?: MergeNodeOption;
}

interface LibraryCategory {
  rank: number;
  label: string;
  icon: string;
  catColor: string;
  items: LibraryItem[];
}

const TRANSFORM_META: Record<string, { abbr: string; color: string }> = {
  'fhir-validation':  { abbr: 'VAL', color: '#10B981' },
  'normalize':        { abbr: 'NRM', color: '#3B82F6' },
  'patient-matching': { abbr: 'MPI', color: '#3B82F6' },
  'merge-patients':   { abbr: 'MRG', color: '#3B82F6' },
  'terminology':      { abbr: 'TRM', color: '#F59E0B' },
  'deid-safeharbor':  { abbr: 'DEI', color: '#EF4444' },
  'deid-kanon':       { abbr: 'KAN', color: '#EF4444' },
  'field-mapping':    { abbr: 'MAP', color: '#6366F1' },
  'dest-sqlserver':   { abbr: 'SQL', color: '#CC2927' },
  'dest-azuresql':    { abbr: 'AZS', color: '#0078D4' },
  'dest-postgres':    { abbr: 'PG',  color: '#336791' },
  'dest-mysql':       { abbr: 'MY',  color: '#4479A1' },
  'dest-snowflake':   { abbr: 'SNW', color: '#29B5E8' },
  'dest-powerbi':     { abbr: 'PBI', color: '#F2C811' },
  'dest-tableau':     { abbr: 'TAB', color: '#E97627' },
  'dest-databricks':  { abbr: 'DBR', color: '#FF3621' },
  'dest-blob':        { abbr: 'BLB', color: '#0089D6' },
  'dest-s3':          { abbr: 'S3',  color: '#FF9900' },
  'dest-fhir':        { abbr: 'FHR', color: '#00A89D' },
  'dest-csv':         { abbr: 'CSV', color: '#374151' },
  'dest-xlsx':        { abbr: 'XLS', color: '#217346' },
  'dest-ndjson':      { abbr: 'NDJ', color: '#475569' },
  'dest-parquet':     { abbr: 'PAR', color: '#64748B' },
  'dest-avro':        { abbr: 'AVR', color: '#64748B' },
  'dest-protobuf':    { abbr: 'PRT', color: '#64748B' },
  'dest-pdf':         { abbr: 'PDF', color: '#DC2626' },
  'dest-sftp':        { abbr: 'FTP', color: '#475569' },
  'dest-restapi':     { abbr: 'API', color: '#475569' },
  'dest-inmemory':    { abbr: 'MEM', color: '#94A3B8' },
  'audit-lineage':    { abbr: 'AUD', color: '#0EA5E9' },
  'hedis':            { abbr: 'HDI', color: '#D946EF' },
  'anomaly':          { abbr: 'ANO', color: '#D946EF' },
  'patient-agg':      { abbr: 'AGG', color: '#D946EF' },
};

const RANK_META: Record<number, { icon: string; catColor: string }> = {
  0: { icon: '⬡', catColor: '#00A89D' },
  1: { icon: '⊙', catColor: '#8B5CF6' },
  2: { icon: '✓', catColor: '#10B981' },
  3: { icon: '⇄', catColor: '#3B82F6' },
  4: { icon: '⌘', catColor: '#F59E0B' },
  5: { icon: '⊘', catColor: '#EF4444' },
  6: { icon: '⚡', catColor: '#6366F1' },
  7: { icon: '▶', catColor: '#64748B' },
  8: { icon: '◎', catColor: '#0EA5E9' },
  9: { icon: '◈', catColor: '#D946EF' },
};

@Component({
  selector: 'app-node-library-dialog',
  standalone: true,
  imports: [ModalOverlayComponent],
  templateUrl: './node-library-dialog.component.html',
  styleUrl: './node-library-dialog.component.scss',
})
export class NodeLibraryDialogComponent {
  private readonly store    = inject(PipelineStore);
  private readonly appSvc   = inject(ApplicabilityService);
  private readonly phaseCfg = inject(PhaseConfigService);

  readonly open         = input(false);
  readonly mode         = input<LibraryMode>('source');
  readonly originNodeId = input<string | null>(null);

  readonly closed            = output<void>();
  readonly sourceSelected    = output<string>();
  readonly transformSelected = output<AddTransformEvent>();
  readonly mergeSelected     = output<MergeEvent>();

  // ── local UI state ────────────────────────────────────────────────────────
  readonly searchQuery   = signal('');
  readonly selectedId    = signal<string | null>(null);
  readonly expandedRanks = signal<Set<number>>(new Set([0, 2, 3, 4, 5, 6, 7]));
  readonly showHidden    = signal(false);

  // ── picker model (transform mode only) ───────────────────────────────────
  private readonly pickerModel = computed(() => {
    if (this.mode() !== 'transform') return null;
    const id = this.originNodeId();
    if (!id) return null;
    const node = this.store.byId(id);
    if (!node) return null;
    return this.appSvc.pickerModel(
      node,
      this.store.nodes(),
      this.store.edges(),
      this.showHidden(),
    );
  });

  readonly pickerMeta = computed(() => this.pickerModel());

  // ── all categories ────────────────────────────────────────────────────────
  private readonly allCategories = computed<LibraryCategory[]>(() => {
    const m = this.mode();
    const pm = this.pickerModel();
    const byRank = new Map<number, LibraryItem[]>();

    // Rank 0 — Sources (filtered by phase config: only enabled sources shown)
    const visibleSources = SOURCES.filter(s => this.phaseCfg.isSourceEnabled(s.id));
    byRank.set(0, visibleSources.map(s => ({
      id:       s.id,
      rank:     0,
      name:     s.name,
      sub:      s.sub,
      abbr:     s.abbr,
      color:    s.color,
      category: null,
      isSource: true,
      status:   (m === 'source' ? 'enabled' : 'disabled') as ItemStatus,
    })));

    // Rank 1-9 — Transforms (hidden ranks and disabled items filtered by phase config)
    const pickerMap = new Map(pm?.items.map(i => [i.id, i]) ?? []);

    TRANSFORMS.forEach(t => {
      // Skip entire rank categories hidden by phase config
      if (this.phaseCfg.isRankHidden(t.rank)) return;

      const meta = TRANSFORM_META[t.id] ?? { abbr: t.name.slice(0, 3).toUpperCase(), color: '#64748B' };
      let status: ItemStatus = 'disabled';
      let reason: string | null = null;

      if (m === 'transform') {
        // Phase config: if not enabled, keep disabled regardless of pipeline state
        if (!this.phaseCfg.isTransformEnabled(t.id)) {
          status = 'disabled';
          reason = 'Not available in this phase';
        } else {
          const pi = pickerMap.get(t.id);
          status = pi ? (pi.status as ItemStatus) : 'disabled';
          reason = pi?.reason ?? null;
        }
      }

      const item: LibraryItem = {
        id: t.id, rank: t.rank, name: t.name, sub: t.sub,
        abbr: meta.abbr, color: meta.color,
        category: t.category ?? null,
        isSource: false, status, reason,
        group: t.group ?? null,
      };

      if (!byRank.has(t.rank)) byRank.set(t.rank, []);
      byRank.get(t.rank)!.push(item);
    });

    // Merge option (transform mode only)
    if (m === 'transform' && pm?.mergeOpt) {
      const opt = pm.mergeOpt;
      const groupRank = TRANSFORMS.find(t => t.group === opt.group)?.rank ?? 3;
      const mergeItem: LibraryItem = {
        id:       `__merge__${opt.group}`,
        rank:     groupRank,
        name:     opt.source ? 'Merge Sources' : `Merge · ${this.appSvc.groupLabel(opt.group)}`,
        sub:      `Fan ${opt.count} ${opt.source ? 'source connectors' : 'group members'} into one merge node.`,
        abbr:     '⊕',
        color:    '#10B981',
        category: null,
        isSource: false,
        status:   'show',
        isMerge:  true,
        mergeOpt: opt,
        group:    opt.group,
      };
      if (!byRank.has(groupRank)) byRank.set(groupRank, []);
      byRank.get(groupRank)!.push(mergeItem);
    }

    return [...byRank.entries()]
      .sort(([a], [b]) => a - b)
      .filter(([, items]) => items.length > 0)
      .map(([rank, items]) => ({
        rank,
        label:    RANK_LABEL[rank] ?? `Rank ${rank}`,
        icon:     RANK_META[rank]?.icon ?? '◉',
        catColor: RANK_META[rank]?.catColor ?? '#64748B',
        items,
      }));
  });

  // ── search-filtered categories ────────────────────────────────────────────
  readonly filteredCategories = computed<LibraryCategory[]>(() => {
    const q = this.searchQuery().trim().toLowerCase();
    if (!q) return this.allCategories();
    return this.allCategories()
      .map(cat => ({ ...cat, items: cat.items.filter(i =>
        i.name.toLowerCase().includes(q) || i.sub.toLowerCase().includes(q)
      )}))
      .filter(cat => cat.items.length > 0);
  });

  // ── selected item (resolved from id) ─────────────────────────────────────
  readonly selectedItem = computed<LibraryItem | undefined>(() => {
    const id = this.selectedId();
    if (!id) return undefined;
    for (const cat of this.allCategories()) {
      const found = cat.items.find(i => i.id === id);
      if (found) return found;
    }
    return undefined;
  });

  readonly rankLabel = RANK_LABEL;

  // ── template helpers ──────────────────────────────────────────────────────
  isCatExpanded(rank: number): boolean {
    if (this.searchQuery().trim()) return true;
    return this.expandedRanks().has(rank);
  }

  hasCatSelected(cat: LibraryCategory): boolean {
    return cat.items.some(i => i.id === this.selectedId());
  }

  getRankColor(rank: number): string {
    return RANK_META[rank]?.catColor ?? '#64748B';
  }

  toggleCat(rank: number): void {
    this.expandedRanks.update(s => {
      const next = new Set(s);
      next.has(rank) ? next.delete(rank) : next.add(rank);
      return next;
    });
  }

  selectItem(item: LibraryItem): void {
    if (item.status === 'disabled' || item.status === 'hide') return;
    this.selectedId.set(item.id);
  }

  // ── add to pipeline ───────────────────────────────────────────────────────
  addSelected(): void {
    const item = this.selectedItem();
    if (!item) return;

    if (item.isSource) {
      this._close();
      this.sourceSelected.emit(item.id);
      return;
    }

    if (item.isMerge && item.mergeOpt) {
      this._close();
      this.mergeSelected.emit({ opt: item.mergeOpt });
      return;
    }

    const pm = this.pickerModel();
    if (!pm) return;
    this._close();
    this.transformSelected.emit({ attachNode: pm.attachTo, transformId: item.id, status: item.status });
  }

  close(): void { this._close(); }

  private _close(): void {
    this.closed.emit();
    this.selectedId.set(null);
    this.searchQuery.set('');
  }
}
