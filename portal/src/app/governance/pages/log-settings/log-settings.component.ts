import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatTableModule } from '@angular/material/table';
import { GovernanceApiService } from '../../services/governance-api.service';
import { LogSettings } from '../../models/governance.model';

@Component({
  selector: 'app-log-settings',
  standalone: true,
  imports: [CommonModule, MatTableModule],
  templateUrl: './log-settings.component.html',
  styleUrl: './log-settings.component.scss',
})
export class LogSettingsComponent implements OnInit {
  private readonly api = inject(GovernanceApiService);

  readonly loading = signal(false);
  readonly settings = signal<LogSettings | null>(null);

  readonly displayedCols = ['category', 'writesTo', 'description'];

  ngOnInit(): void {
    this.loading.set(true);
    this.api.logSettings().subscribe({
      next: settings => { this.settings.set(settings); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }
}
