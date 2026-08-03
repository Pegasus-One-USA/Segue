import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatTableModule } from '@angular/material/table';
import { OperationsApiService } from '../../services/operations-api.service';
import { ComponentHealth } from '../../models/operations.model';

@Component({
  selector: 'app-system-health-page',
  standalone: true,
  imports: [CommonModule, MatTableModule],
  templateUrl: './system-health.component.html',
  styleUrl: './system-health.component.scss',
})
export class SystemHealthPageComponent implements OnInit {
  private readonly api = inject(OperationsApiService);

  readonly loading = signal(false);
  readonly components = signal<ComponentHealth[]>([]);

  readonly displayedCols = ['component', 'status', 'cpuPercent', 'memoryBytes', 'notes'];

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.systemHealth().subscribe({
      next: result => { this.components.set(result.components); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  formatBytes(bytes: number | null): string {
    if (bytes === null) return '—';
    return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
  }
}
