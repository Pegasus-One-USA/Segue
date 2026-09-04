import { Component, input, output, inject, computed, signal } from '@angular/core';
import { ModalOverlayComponent } from '../../shared/modal-overlay/modal-overlay.component';
import { PipelineStoreV2 } from '../../../services/pipeline-v2.store';
import { ApplicabilityServiceV2 } from '../../../services/applicability-v2.service';
import { TRANSFORMS } from '../../../data/transforms-v2.data';
import { RANK_LABEL } from '../../../models/transform-v2.model';
import { CanvasNode } from '../../../models/node-v2.model';
import { PickerItem, MergeNodeOption } from '../../../models/wizard-state-v2.model';
import { ToastService } from '../../../services/toast.service';

export interface AddTransformEvent {
  attachNode: CanvasNode;
  transformId: string;
  status: string;
}

export interface MergeEvent {
  opt: MergeNodeOption;
}

@Component({
  selector: 'app-transform-picker',
  standalone: true,
  imports: [ModalOverlayComponent],
  templateUrl: './transform-picker.component.html',
  styleUrl: './transform-picker.component.scss',
})
export class TransformPickerComponent {
  private readonly store   = inject(PipelineStoreV2);
  private readonly appSvc  = inject(ApplicabilityServiceV2);
  private readonly toast   = inject(ToastService);

  readonly open        = input(false);
  readonly originNodeId = input<string | null>(null);
  readonly closed      = output<void>();
  readonly addTransform = output<AddTransformEvent>();
  readonly addMerge    = output<MergeEvent>();

  protected readonly showHidden = signal(false);

  protected readonly model = computed(() => {
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

  protected readonly rankLabel = RANK_LABEL;

  protected groupedItems(): { rank: number; label: string; items: PickerItem[] }[] {
    const m = this.model();
    if (!m) return [];
    const byRank = new Map<number, PickerItem[]>();
    m.items.forEach(it => {
      if (!byRank.has(it.rank)) byRank.set(it.rank, []);
      byRank.get(it.rank)!.push(it);
    });
    return [...byRank.entries()]
      .sort(([a], [b]) => a - b)
      .map(([rank, items]) => ({
        rank,
        label: RANK_LABEL[rank] ?? String(rank),
        items,
      }));
  }

  protected groupLabelOf(items: PickerItem[]): string | null {
    const g = items.find(i => i.group)?.group;
    return g ? this.appSvc.groupLabel(g) : null;
  }

  protected categoriesOf(items: PickerItem[]): { cat: string | null; items: PickerItem[] }[] {
    const cats: { cat: string | null; items: PickerItem[] }[] = [];
    items.forEach(it => {
      const t = TRANSFORMS.find(x => x.id === it.id);
      const cat = t?.category ?? null;
      let bucket = cats.find(c => c.cat === cat);
      if (!bucket) { bucket = { cat, items: [] }; cats.push(bucket); }
      bucket.items.push(it);
    });
    return cats;
  }

  select(item: PickerItem): void {
    if (item.status === 'hide') return;
    const m = this.model();
    if (!m) return;
    this.closed.emit();
    this.addTransform.emit({ attachNode: m.attachTo, transformId: item.id, status: item.status });
  }

  selectMerge(): void {
    const m = this.model();
    if (!m?.mergeOpt) return;
    this.closed.emit();
    this.addMerge.emit({ opt: m.mergeOpt });
  }
}
