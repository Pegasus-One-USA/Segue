import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatTableModule } from '@angular/material/table';
import { GovernanceApiService } from '../../services/governance-api.service';
import { RetentionPolicyEntry } from '../../models/governance.model';

@Component({
  selector: 'app-retention-policies',
  standalone: true,
  imports: [CommonModule, MatTableModule],
  templateUrl: './retention-policies.component.html',
  styleUrl: './retention-policies.component.scss',
})
export class RetentionPoliciesComponent implements OnInit {
  private readonly api = inject(GovernanceApiService);

  readonly loading = signal(false);
  readonly entries = signal<RetentionPolicyEntry[]>([]);

  readonly displayedCols = ['dataClass', 'retentionYears', 'purgeable'];

  ngOnInit(): void {
    this.loading.set(true);
    this.api.retentionPolicies().subscribe({
      next: entries => { this.entries.set(entries); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }
}
