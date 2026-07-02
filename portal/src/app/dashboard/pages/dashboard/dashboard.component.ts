import { Component, inject } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { DashboardService } from '../../services/dashboard.service';
import { PipelineRunService } from '../../services/pipeline-run.service';
import { PipelineTableComponent } from '../../components/pipeline-table/pipeline-table.component';
import { ActivityFeedComponent } from '../../components/activity-feed/activity-feed.component';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [
    DatePipe,
    RouterLink,
    PipelineTableComponent,
    ActivityFeedComponent,
  ],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss',
})
export class DashboardComponent {
  private readonly dashSvc = inject(DashboardService);
  private readonly runSvc  = inject(PipelineRunService);

  protected readonly activity      = this.dashSvc.activity;
  protected readonly runs          = this.runSvc.runs;
  protected readonly lastRefreshed = this.dashSvc.lastRefreshed;

  refresh(): void {
    this.dashSvc.refresh();
    this.runSvc.fetchRecent();
  }

  onActionClick(_id: string): void {}
}
