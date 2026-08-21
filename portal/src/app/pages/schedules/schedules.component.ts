import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { MatTableModule } from '@angular/material/table';
import { OperationsApiService } from '../../operations/services/operations-api.service';
import { SchedulerSummaryEntry } from '../../operations/models/operations.model';

@Component({
  selector: 'app-schedules',
  standalone: true,
  imports: [CommonModule, DatePipe, MatTableModule],
  templateUrl: './schedules.component.html',
  styleUrl: './schedules.component.scss',
})
export class SchedulesComponent implements OnInit {
  private readonly api = inject(OperationsApiService);

  readonly loading = signal(false);
  readonly entries = signal<SchedulerSummaryEntry[]>([]);

  readonly displayedCols = ['routeName', 'scheduleExpression', 'nextRunUtc', 'lastRunStartedUtc', 'lastRunDurationMs', 'lastRunStatus'];

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.schedulerSummary().subscribe({
      next: entries => { this.entries.set(entries); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }
}
