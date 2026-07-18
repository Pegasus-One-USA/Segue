import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { MatTableModule } from '@angular/material/table';
import { GovernanceApiService } from '../../services/governance-api.service';
import { SecurityEventEntry } from '../../models/governance.model';

@Component({
  selector: 'app-security-events',
  standalone: true,
  imports: [CommonModule, DatePipe, MatTableModule],
  templateUrl: './security-events.component.html',
  styleUrl: './security-events.component.scss',
})
export class SecurityEventsComponent implements OnInit {
  private readonly api = inject(GovernanceApiService);
  private readonly route = inject(ActivatedRoute);

  readonly loading = signal(false);
  readonly correlationId = signal('');
  readonly entries = signal<SecurityEventEntry[]>([]);

  readonly displayedCols = ['occurredOnUtc', 'severity', 'eventType', 'userEmail', 'ipAddress', 'resolved', 'correlationId'];

  ngOnInit(): void {
    const fromQuery = this.route.snapshot.queryParamMap.get('correlationId');
    if (fromQuery) {
      this.correlationId.set(fromQuery);
    }
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.securityEvents(this.correlationId() || undefined).subscribe({
      next: entries => { this.entries.set(entries); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  onCorrelationIdChange(value: string): void {
    this.correlationId.set(value);
  }

  reset(): void {
    this.correlationId.set('');
    this.load();
  }

  severityClass(severity: string): string {
    return 'severity-' + severity.toLowerCase();
  }
}
