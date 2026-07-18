import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { MatTableModule } from '@angular/material/table';
import { GovernanceApiService } from '../../services/governance-api.service';
import { SmartLaunchLogEntry } from '../../models/governance.model';

@Component({
  selector: 'app-smart-launch-logs',
  standalone: true,
  imports: [CommonModule, DatePipe, MatTableModule],
  templateUrl: './smart-launch-logs.component.html',
  styleUrl: './smart-launch-logs.component.scss',
})
export class SmartLaunchLogsComponent implements OnInit {
  private readonly api = inject(GovernanceApiService);

  readonly loading = signal(false);
  readonly entries = signal<SmartLaunchLogEntry[]>([]);

  readonly displayedCols = ['occurredOnUtc', 'sourceName', 'launchType', 'success', 'failureReason'];

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.smartLaunchLogs().subscribe({
      next: entries => { this.entries.set(entries); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }
}
