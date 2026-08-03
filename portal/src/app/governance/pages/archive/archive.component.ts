import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatTableModule } from '@angular/material/table';
import { GovernanceApiService } from '../../services/governance-api.service';
import { ArchiveManifestEntry } from '../../models/governance.model';

@Component({
  selector: 'app-archive',
  standalone: true,
  imports: [CommonModule, MatTableModule],
  templateUrl: './archive.component.html',
  styleUrl: './archive.component.scss',
})
export class ArchiveComponent implements OnInit {
  private readonly api = inject(GovernanceApiService);

  readonly loading = signal(false);
  readonly entries = signal<ArchiveManifestEntry[]>([]);
  readonly restoreMessage = signal<string | null>(null);

  readonly displayedCols = ['dataClass', 'archivedThroughUtc', 'recordCount', 'fileLocation', 'restore'];

  ngOnInit(): void {
    this.loading.set(true);
    this.api.archives().subscribe({
      next: entries => { this.entries.set(entries); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  restore(dataClass: string): void {
    this.restoreMessage.set(null);
    this.api.restoreArchive(dataClass).subscribe({
      next: () => this.restoreMessage.set(`Restored ${dataClass}.`),
      error: err => this.restoreMessage.set(
        typeof err?.error?.error === 'string' ? err.error.error : `Restore for ${dataClass} is not available.`,
      ),
    });
  }
}
