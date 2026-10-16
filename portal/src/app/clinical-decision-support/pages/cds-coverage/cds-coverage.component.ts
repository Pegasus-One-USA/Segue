import { Component, OnInit, computed, inject, signal, viewChild } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { catchError, of } from 'rxjs';
import { CdsCoverageApiService } from '../../services/cds-coverage-api.service';
import { CdsApiService } from '../../services/cds-api.service';
import { CDS_COVERAGE_HANDLER_KEY, CdsPayer } from '../../models/cds-coverage.models';
import { CdsServiceInstance } from '../../models/cds.models';
import { CdsPayerListComponent } from '../../components/cds-payer-list/cds-payer-list.component';
import { CdsCoverageRuleListComponent } from '../../components/cds-coverage-rule-list/cds-coverage-rule-list.component';
import { ToastService } from '../../../services/toast.service';

/**
 * Coverage (CRD): the payers Segue answers coverage requirements for and their rules. A "Coverage requirements"
 * service (added on Services) reads them when the EHR asks while a clinician orders.
 */
@Component({
  selector: 'app-cds-coverage',
  standalone: true,
  imports: [RouterLink, MatIconModule, CdsPayerListComponent, CdsCoverageRuleListComponent],
  templateUrl: './cds-coverage.component.html',
  styleUrl: './cds-coverage.component.scss',
})
export class CdsCoverageComponent implements OnInit {
  private readonly coverageApi = inject(CdsCoverageApiService);
  private readonly cdsApi      = inject(CdsApiService);
  private readonly toast       = inject(ToastService);

  private readonly ruleList = viewChild(CdsCoverageRuleListComponent);

  readonly loading  = signal(true);
  readonly payers   = signal<CdsPayer[]>([]);
  readonly services = signal<CdsServiceInstance[] | null>(null);

  /** Null while unknown (the list could not be read), so no hint is shown by mistake. */
  readonly hasCoverageService = computed(() => {
    const services = this.services();
    return services === null ? null : services.some(s => s.handlerKey === CDS_COVERAGE_HANDLER_KEY);
  });

  ngOnInit(): void {
    this.loadPayers();
    this.cdsApi.getServices().pipe(catchError(() => of(null))).subscribe(services => this.services.set(services));
  }

  loadPayers(): void {
    this.coverageApi.getPayers().subscribe({
      next: payers => {
        this.payers.set([...payers].sort((a, b) => a.name.localeCompare(b.name)));
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.toast.error('Could not load the payers.');
      },
    });
  }

  /** A payer was added, edited or deleted: rule rows show payer names, so they reload too. */
  onPayersChanged(): void {
    this.loadPayers();
    this.ruleList()?.load();
  }
}
