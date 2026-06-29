import { Component, computed, input } from '@angular/core';
import { PipelineRun } from '../../models/pipeline-run.model';

interface DonutSegment {
  status:      string;
  label:       string;
  color:       string;
  count:       number;
  pct:         number;
  dasharray:   string;
  dashoffset:  string;
}

const DEFS = [
  { status: 'completed', label: 'Completed', color: '#10b981' },
  { status: 'running',   label: 'Running',   color: '#3b7fff' },
  { status: 'failed',    label: 'Failed',    color: '#ef4444' },
  { status: 'queued',    label: 'Queued',    color: '#f59e0b' },
] as const;

@Component({
  selector: 'app-execution-status',
  standalone: true,
  imports: [],
  templateUrl: './execution-status.component.html',
  styleUrl: './execution-status.component.scss',
})
export class ExecutionStatusComponent {
  readonly runs = input<PipelineRun[]>([]);

  readonly total = computed(() => this.runs().length);

  readonly segments = computed<DonutSegment[]>(() => {
    const rs    = this.runs();
    const total = rs.length || 1;
    const C     = 2 * Math.PI * 44;
    let   cum   = 0;

    return DEFS.map(d => {
      const count = rs.filter(n => n.status === d.status).length;
      const len   = (count / total) * C;
      const seg: DonutSegment = {
        ...d,
        count,
        pct:        Math.round((count / total) * 100),
        dasharray:  `${len.toFixed(2)} ${(C - len).toFixed(2)}`,
        dashoffset: `${(-cum).toFixed(2)}`,
      };
      cum += len;
      return seg;
    });
  });
}
