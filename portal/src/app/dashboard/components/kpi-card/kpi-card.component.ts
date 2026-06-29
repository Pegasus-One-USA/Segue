import { Component, computed, input } from '@angular/core';
import { KpiMetric } from '../../models/kpi-metric.model';

@Component({
  selector: 'app-kpi-card',
  standalone: true,
  imports: [],
  templateUrl: './kpi-card.component.html',
  styleUrl: './kpi-card.component.scss',
})
export class KpiCardComponent {
  readonly metric = input.required<KpiMetric>();

  readonly trendClass = computed(() => {
    const d = this.metric().trend?.direction;
    return d ? `trend-${d}` : '';
  });

  readonly trendArrow = computed(() => {
    const d = this.metric().trend?.direction;
    return d ? ({ up: '↑', down: '↓', flat: '→' } as const)[d] : '';
  });
}
