import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { MatTableModule } from '@angular/material/table';
import { OperationsApiService } from '../../services/operations-api.service';
import { EndpointHealthCheckEntry } from '../../models/operations.model';

@Component({
  selector: 'app-endpoint-health',
  standalone: true,
  imports: [CommonModule, DatePipe, MatTableModule],
  templateUrl: './endpoint-health.component.html',
  styleUrl: './endpoint-health.component.scss',
})
export class EndpointHealthComponent implements OnInit {
  private readonly api = inject(OperationsApiService);

  readonly loading = signal(false);
  readonly entries = signal<EndpointHealthCheckEntry[]>([]);

  readonly displayedCols = ['occurredOnUtc', 'endpointName', 'endpointType', 'status', 'latencyMs', 'message'];

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.endpointHealth().subscribe({
      next: entries => { this.entries.set(entries); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  statusClass(status: string): string {
    return status === 'Healthy' ? 'status-success' : 'status-fail';
  }
}
