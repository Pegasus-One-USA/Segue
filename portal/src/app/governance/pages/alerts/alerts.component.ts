import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { MatTableModule } from '@angular/material/table';
import { GovernanceApiService } from '../../services/governance-api.service';
import { AlertHistoryEntry } from '../../models/governance.model';

@Component({
  selector: 'app-alerts',
  standalone: true,
  imports: [CommonModule, DatePipe, MatTableModule],
  templateUrl: './alerts.component.html',
  styleUrl: './alerts.component.scss',
})
export class AlertsComponent implements OnInit {
  private readonly api = inject(GovernanceApiService);

  readonly loading = signal(false);
  readonly entries = signal<AlertHistoryEntry[]>([]);

  readonly displayedCols = ['firedOnUtc', 'ruleName', 'severity', 'summary', 'acknowledged'];

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.alertHistory().subscribe({
      next: entries => { this.entries.set(entries); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  acknowledge(entry: AlertHistoryEntry): void {
    this.api.acknowledgeAlert(entry.id).subscribe({ next: () => this.load() });
  }
}
