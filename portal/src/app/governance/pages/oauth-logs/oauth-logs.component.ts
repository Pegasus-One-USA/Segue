import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { MatTableModule } from '@angular/material/table';
import { GovernanceApiService } from '../../services/governance-api.service';
import { AuthenticationLogEntry } from '../../models/governance.model';

@Component({
  selector: 'app-oauth-logs',
  standalone: true,
  imports: [CommonModule, DatePipe, MatTableModule],
  templateUrl: './oauth-logs.component.html',
  styleUrl: './oauth-logs.component.scss',
})
export class OAuthLogsComponent implements OnInit {
  private readonly api = inject(GovernanceApiService);

  readonly loading = signal(false);
  readonly entries = signal<AuthenticationLogEntry[]>([]);

  readonly displayedCols = ['occurredOnUtc', 'authenticationType', 'userEmail', 'success', 'failureReason', 'correlationId'];

  ngOnInit(): void {
    this.loading.set(true);
    this.api.authenticationLogs(undefined, 200, 'OAuth').subscribe({
      next: entries => { this.entries.set(entries); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }
}
