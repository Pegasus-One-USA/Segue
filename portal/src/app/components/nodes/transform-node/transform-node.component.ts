import { Component, input, output } from '@angular/core';
import { CanvasNode, TransformNode } from '../../../models/node.model';
import { TRANSFORMS } from '../../../data/transforms.data';

const TRANSFORM_META: Record<string, { abbr: string; color: string }> = {
  'fhir-validation':  { abbr: 'VAL', color: '#6366F1' },
  'normalize':        { abbr: 'NRM', color: '#8B5CF6' },
  'patient-matching': { abbr: 'MPI', color: '#8B5CF6' },
  'merge-patients':   { abbr: 'MRG', color: '#8B5CF6' },
  'terminology':      { abbr: 'TRM', color: '#F59E0B' },
  'deid-safeharbor':  { abbr: 'DEI', color: '#EF4444' },
  'deid-kanon':       { abbr: 'KAN', color: '#EF4444' },
  'field-mapping':    { abbr: 'MAP', color: '#6366F1' },
  'dest-sqlserver':   { abbr: 'SQL', color: '#CC2927' },
  'dest-azuresql':    { abbr: 'AZS', color: '#0078D4' },
  'dest-postgres':    { abbr: 'PG',  color: '#336791' },
  'dest-mysql':       { abbr: 'MY',  color: '#4479A1' },
  'dest-mongo':       { abbr: 'MDB', color: '#47A248' },
  'dest-snowflake':   { abbr: 'SNW', color: '#29B5E8' },
  'dest-powerbi':     { abbr: 'PBI', color: '#F2C811' },
  'dest-tableau':     { abbr: 'TAB', color: '#E97627' },
  'dest-databricks':  { abbr: 'DBR', color: '#FF3621' },
  'dest-blob':        { abbr: 'BLB', color: '#0089D6' },
  'dest-s3':          { abbr: 'S3',  color: '#FF9900' },
  'dest-fhir':        { abbr: 'FHR', color: '#00A89D' },
  'dest-medplum':     { abbr: 'MP',  color: '#00A89D' },
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

@Component({
  selector: 'app-transform-node',
  standalone: true,
  imports: [],
  templateUrl: './transform-node.component.html',
  styleUrl: './transform-node.component.scss',
  host: { class: 'node', '[style.left.px]': 'node().x', '[style.top.px]': 'node().y' },
})
export class TransformNodeComponent {
  readonly node = input.required<CanvasNode>();
  /** Hides the delete/add-next buttons — set by canvas.component from the builder's canMutate(). */
  readonly readOnly = input(false);

  readonly delete    = output<string>();
  readonly addNext   = output<string>();
  readonly configure = output<string>();
  readonly dragMove  = output<{ nodeId: string; x: number; y: number }>();
  readonly dragEnd   = output<{ nodeId: string; x: number; y: number }>();
  readonly portStart = output<{ nodeId: string; fromX: number; fromY: number }>();
  readonly portMove  = output<{ clientX: number; clientY: number }>();
  readonly portEnd   = output<{ nodeId: string; clientX: number; clientY: number }>();

  protected transformId(): string {
    return (this.node() as TransformNode).transformId ?? '';
  }

  protected abbr(): string {
    return TRANSFORM_META[this.transformId()]?.abbr ?? this.transformId().slice(0, 3).toUpperCase();
  }

  protected nodeColor(): string {
    return TRANSFORM_META[this.transformId()]?.color ?? '#6366F1';
  }

  protected transformName(): string {
    const t = TRANSFORMS.find(x => x.id === this.transformId());
    return t?.name ?? this.transformId();
  }

  protected sourceName(): string {
    return (this.node() as TransformNode).sourceName ?? 'Epic';
  }

  protected isCaveat(): boolean {
    return (this.node() as TransformNode).statusAtAdd === 'caveat';
  }

  protected isDestination(): boolean {
    return this.transformId().startsWith('dest-');
  }

  /** Field Mapping nodes aren't independently removable from the canvas via this button — by product
   *  decision, not a technical restriction. */
  protected isMapping(): boolean {
    return this.transformId() === 'field-mapping';
  }

  onAddNextClick(e: MouseEvent): void {
    e.stopPropagation();
    this.addNext.emit(this.node().id);
  }

  onDeleteClick(e: MouseEvent): void {
    e.stopPropagation();
    this.delete.emit(this.node().id);
  }

  // ── drag (tracks distance to distinguish click from drag) ────────────────
  private drag: { sX: number; sY: number; nX: number; nY: number; dragged: boolean } | null = null;

  onCirclePointerDown(e: PointerEvent): void {
    if (e.button !== 0) return; e.stopPropagation();
    this.drag = { sX: e.clientX, sY: e.clientY, nX: this.node().x, nY: this.node().y, dragged: false };
    (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
  }

  onCirclePointerMove(e: PointerEvent): void {
    if (!this.drag) return;
    const dx = e.clientX - this.drag.sX;
    const dy = e.clientY - this.drag.sY;
    if (Math.abs(dx) > 4 || Math.abs(dy) > 4) {
      this.drag.dragged = true;
      this.dragMove.emit({ nodeId: this.node().id, x: Math.round(this.drag.nX + dx), y: Math.round(this.drag.nY + dy) });
    }
  }

  onCirclePointerUp(e: PointerEvent): void {
    if (!this.drag) return;
    try { (e.currentTarget as HTMLElement).releasePointerCapture(e.pointerId); } catch { /* pointer already released */ }
    if (!this.drag.dragged) {
      this.configure.emit(this.node().id);
    }
    this.drag = null;
  }

  // ── port ──────────────────────────────────────────────────────────────────
  onPortPointerDown(e: PointerEvent): void {
    e.stopPropagation(); e.preventDefault();
    this.portStart.emit({ nodeId: this.node().id, fromX: this.node().x, fromY: this.node().y });
    const move = (ev: PointerEvent) => this.portMove.emit({ clientX: ev.clientX, clientY: ev.clientY });
    const up   = (ev: PointerEvent) => {
      document.removeEventListener('pointermove', move);
      document.removeEventListener('pointerup', up);
      this.portEnd.emit({ nodeId: this.node().id, clientX: ev.clientX, clientY: ev.clientY });
    };
    document.addEventListener('pointermove', move);
    document.addEventListener('pointerup', up);
  }
}
