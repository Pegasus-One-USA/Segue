import { Component, input } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ActivityEvent, ActivitySeverity } from '../../models/activity-event.model';

const SEVERITY_ICON: Record<ActivitySeverity, string> = {
  success: '✓',
  info:    'ℹ',
  warning: '⚠',
  error:   '✕',
};

@Component({
  selector: 'app-activity-feed',
  standalone: true,
  imports: [DatePipe, RouterLink],
  templateUrl: './activity-feed.component.html',
  styleUrl: './activity-feed.component.scss',
})
export class ActivityFeedComponent {
  readonly events = input<ActivityEvent[]>([]);

  icon(sev: ActivitySeverity): string {
    return SEVERITY_ICON[sev];
  }
}
