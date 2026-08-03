import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { MatTableModule } from '@angular/material/table';
import { OperationsApiService } from '../../services/operations-api.service';
import { QueueDepthEntry } from '../../models/operations.model';

@Component({
  selector: 'app-queue-monitor',
  standalone: true,
  imports: [CommonModule, DatePipe, MatTableModule],
  templateUrl: './queue-monitor.component.html',
  styleUrl: './queue-monitor.component.scss',
})
export class QueueMonitorComponent implements OnInit {
  private readonly api = inject(OperationsApiService);

  readonly loading = signal(false);
  readonly entries = signal<QueueDepthEntry[]>([]);

  readonly displayedCols = ['queueName', 'transportType', 'pending', 'processing', 'deadLetter', 'lastMessageUtc'];

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.queueMonitor().subscribe({
      next: entries => { this.entries.set(entries); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }
}
