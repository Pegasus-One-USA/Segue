import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { MatTableModule } from '@angular/material/table';
import { GovernanceApiService } from '../../services/governance-api.service';
import { AuthorizationLogEntry, PagedResult } from '../../models/governance.model';
import { PaginationBarComponent, PageChangeEvent } from '../../../components/shared/pagination-bar/pagination-bar.component';

@Component({
  selector: 'app-authorization-logs',
  standalone: true,
  imports: [CommonModule, DatePipe, MatTableModule, PaginationBarComponent],
  templateUrl: './authorization-logs.component.html',
  styleUrl: './authorization-logs.component.scss',
})
export class AuthorizationLogsComponent implements OnInit {
  private readonly api = inject(GovernanceApiService);
  private readonly route = inject(ActivatedRoute);

  readonly loading = signal(false);
  readonly correlationId = signal('');
  readonly result = signal<PagedResult<AuthorizationLogEntry>>({ items: [], totalCount: 0, page: 1, pageSize: 25 });
  readonly pageIndex = signal(0);
  readonly pageSize = signal(10);

  readonly displayedCols = ['occurredOnUtc', 'userEmail', 'requestPath', 'permissionCode', 'result', 'ipAddress', 'correlationId'];

  ngOnInit(): void {
    const fromQuery = this.route.snapshot.queryParamMap.get('correlationId');
    if (fromQuery) {
      this.correlationId.set(fromQuery);
    }
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.authorizationLogs(this.correlationId() || undefined, this.pageIndex() + 1, this.pageSize()).subscribe({
      next: result => { this.result.set(result); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  onPageChange(e: PageChangeEvent): void {
    this.pageIndex.set(e.pageIndex);
    this.pageSize.set(e.pageSize);
    this.load();
  }

  onCorrelationIdChange(value: string): void {
    this.correlationId.set(value);
  }

  reset(): void {
    this.correlationId.set('');
    this.pageIndex.set(0);
    this.load();
  }
}
