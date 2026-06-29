import { Component, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { DashboardService } from '../../services/dashboard.service';
import { PipelineRunService } from '../../services/pipeline-run.service';
import { SidebarComponent } from '../../layout/sidebar/sidebar.component';
import { KpiCardComponent } from '../../components/kpi-card/kpi-card.component';
import { PipelineTableComponent } from '../../components/pipeline-table/pipeline-table.component';
import { ExecutionStatusComponent } from '../../components/execution-status/execution-status.component';
import { SystemHealthComponent } from '../../components/system-health/system-health.component';
import { QuickActionsComponent } from '../../components/quick-actions/quick-actions.component';
import { ActivityFeedComponent } from '../../components/activity-feed/activity-feed.component';
import { UserMenuComponent } from '../../../user/components/user-menu/user-menu.component';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [
    DatePipe,
    RouterLink,
    SidebarComponent,
    KpiCardComponent,
    PipelineTableComponent,
    ExecutionStatusComponent,
    SystemHealthComponent,
    QuickActionsComponent,
    ActivityFeedComponent,
    UserMenuComponent,
  ],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss',
})
export class DashboardComponent {
  private readonly dashSvc = inject(DashboardService);
  private readonly runSvc  = inject(PipelineRunService);

  protected readonly sidebarCollapsed = signal(false);

  protected readonly kpis          = this.dashSvc.kpis;
  protected readonly health        = this.dashSvc.health;
  protected readonly activity      = this.dashSvc.activity;
  protected readonly runs          = this.runSvc.runs;
  protected readonly lastRefreshed = this.dashSvc.lastRefreshed;

  toggleSidebar(): void {
    this.sidebarCollapsed.update(v => !v);
  }

  refresh(): void {
    this.dashSvc.refresh();
    this.runSvc.fetchRecent();
  }

  onActionClick(_id: string): void {}
}
