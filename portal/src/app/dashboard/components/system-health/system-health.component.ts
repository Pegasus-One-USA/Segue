import { Component, computed, input } from '@angular/core';
import { HealthCheck, HealthStatus } from '../../models/health-check.model';

@Component({
  selector: 'app-system-health',
  standalone: true,
  imports: [],
  templateUrl: './system-health.component.html',
  styleUrl: './system-health.component.scss',
})
export class SystemHealthComponent {
  readonly checks = input<HealthCheck[]>([]);

  readonly overallStatus = computed<HealthStatus>(() => {
    const cs = this.checks();
    if (cs.some(c => c.status === 'offline'))  return 'offline';
    if (cs.some(c => c.status === 'degraded')) return 'degraded';
    return 'online';
  });

  readonly overallLabel = computed(() => ({
    online:   'All systems operational',
    degraded: 'Partial degradation',
    offline:  'Service disruption',
  }[this.overallStatus()]));
}
