import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { ErrorDashboardComponent } from '../error-dashboard/error-dashboard.component';
import { ErrorsComponent } from '../errors/errors.component';
import { ErrorListTarget } from '../../components/error-detail-dialog/error-detail-dialog.component';

type ErrorsView = 'overview' | 'list';

/**
 * The single "Errors" screen. Two views of the same data, one page:
 *  - Overview: counts, trend, most frequent errors, export, clear, log size and settings.
 *  - All errors: the searchable, filterable list with Resolve / Reopen.
 * Every error opens the same detail popup from either view. The selected view lives in the URL (?view=…), so
 * existing links that carry an error reference or correlation id land on the list, and the browser back button
 * moves between views.
 */
@Component({
  selector: 'app-errors-hub',
  standalone: true,
  imports: [ErrorDashboardComponent, ErrorsComponent],
  templateUrl: './errors-hub.component.html',
  styleUrl: './errors-hub.component.scss',
})
export class ErrorsHubComponent implements OnInit, OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private sub?: Subscription;

  readonly view = signal<ErrorsView>('overview');

  ngOnInit(): void {
    this.sub = this.route.queryParamMap.subscribe(params => {
      const requested = params.get('view');
      const hasFilter = !!params.get('errorReferenceId') || !!params.get('correlationId');
      this.view.set(requested === 'list' || requested === 'overview' ? requested : hasFilter ? 'list' : 'overview');
    });
  }

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
  }

  select(view: ErrorsView): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      // The filters belong to the list; leaving it for the Overview starts the next visit clean.
      queryParams: view === 'overview' ? { view, errorReferenceId: null, correlationId: null } : { view },
      queryParamsHandling: 'merge',
    });
  }

  showInList(target: ErrorListTarget): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        view: 'list',
        errorReferenceId: target.errorReferenceId ?? null,
        correlationId: target.correlationId ?? null,
      },
      queryParamsHandling: 'merge',
    });
  }
}
