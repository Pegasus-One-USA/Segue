import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { MatTableModule } from '@angular/material/table';
import { GovernanceApiService } from '../../services/governance-api.service';
import { AuthenticationLogEntry, PagedResult } from '../../models/governance.model';
import { PaginationBarComponent, PageChangeEvent } from '../../../components/shared/pagination-bar/pagination-bar.component';

@Component({
  selector: 'app-oauth-logs',
  standalone: true,
  imports: [CommonModule, DatePipe, MatTableModule, PaginationBarComponent],
  templateUrl: './oauth-logs.component.html',
  styleUrl: './oauth-logs.component.scss',
})
export class OAuthLogsComponent implements OnInit {
  private readonly api = inject(GovernanceApiService);

  readonly loading = signal(false);
  readonly result = signal<PagedResult<AuthenticationLogEntry>>({ items: [], totalCount: 0, page: 1, pageSize: 25 });
  readonly pageIndex = signal(0);
  readonly pageSize = signal(10);

  readonly displayedCols = ['occurredOnUtc', 'authenticationType', 'userEmail', 'success', 'failureReason', 'correlationId'];

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.authenticationLogs(undefined, this.pageIndex() + 1, this.pageSize(), 'OAuth').subscribe({
      next: result => { this.result.set(result); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  onPageChange(e: PageChangeEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.load();
  }
}
