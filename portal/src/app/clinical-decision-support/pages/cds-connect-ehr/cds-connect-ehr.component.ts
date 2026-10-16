import { Component, OnInit, computed, inject, signal, viewChild } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { catchError, forkJoin, of } from 'rxjs';
import { CdsApiService } from '../../services/cds-api.service';
import { CdsDiscoveryInfo, CdsReadiness, CdsTrustedIssuer } from '../../models/cds.models';
import { CDS_SANDBOX } from '../../models/cds-sandbox';
import { CdsDiscoveryPanelComponent } from '../../components/cds-discovery-panel/cds-discovery-panel.component';
import { CdsSandboxHintComponent } from '../../components/cds-sandbox-hint/cds-sandbox-hint.component';
import { CdsTrustedIssuerListComponent } from '../../components/cds-trusted-issuer-list/cds-trusted-issuer-list.component';
import { CdsEpicSetupSheetComponent } from '../../components/cds-epic-setup-sheet/cds-epic-setup-sheet.component';
import { CdsReadinessPanelComponent } from '../../components/cds-readiness-panel/cds-readiness-panel.component';
import { ToastService } from '../../../services/toast.service';
import { PermissionService } from '../../../auth/services/permission.service';

/**
 * Connect your EHR: go-live readiness, the discovery URL to hand to the EHR team, the trusted issuers, the Epic
 * setup sheet and the sandbox shortcut.
 */
@Component({
  selector: 'app-cds-connect-ehr',
  standalone: true,
  imports: [
    MatIconModule,
    CdsDiscoveryPanelComponent,
    CdsSandboxHintComponent,
    CdsTrustedIssuerListComponent,
    CdsEpicSetupSheetComponent,
    CdsReadinessPanelComponent,
  ],
  templateUrl: './cds-connect-ehr.component.html',
  styleUrl: './cds-connect-ehr.component.scss',
})
export class CdsConnectEhrComponent implements OnInit {
  private readonly api         = inject(CdsApiService);
  private readonly toast       = inject(ToastService);
  private readonly permissions = inject(PermissionService);
  private readonly route       = inject(ActivatedRoute, { optional: true });

  /** ?service=<id>: the service whose Epic setup sheet opens first (linked from the service page). */
  readonly requestedServiceId = this.route?.snapshot.queryParamMap.get('service') ?? null;

  private readonly issuerList = viewChild(CdsTrustedIssuerListComponent);

  readonly loading   = signal(true);
  readonly discovery = signal<CdsDiscoveryInfo | null>(null);
  readonly issuers   = signal<CdsTrustedIssuer[]>([]);
  readonly readiness        = signal<CdsReadiness | null>(null);
  readonly readinessLoading = signal(true);

  readonly canCreate = computed(() => this.permissions.hasPermission('cdshooks.create'));
  readonly sandboxTrusted = computed(() => this.issuers().some(i => i.issuer === CDS_SANDBOX.issuer));

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loadReadiness();
    forkJoin({
      discovery: this.api.getDiscoveryInfo().pipe(catchError(() => of(null))),
      issuers: this.api.getTrustedIssuers().pipe(catchError(() => of(null))),
    }).subscribe(({ discovery, issuers }) => {
      this.discovery.set(discovery);
      if (issuers) this.issuers.set(issuers);
      if (!discovery || !issuers) this.toast.error('Some connection details could not be loaded.');
      this.loading.set(false);
    });
  }

  /** Separate from the rest so a failed report never hides the connection details (and vice versa). */
  loadReadiness(): void {
    this.readinessLoading.set(true);
    this.api.getReadiness().pipe(catchError(() => of(null))).subscribe(readiness => {
      this.readiness.set(readiness);
      this.readinessLoading.set(false);
    });
  }

  addSandbox(): void {
    this.issuerList()?.openAdd(true);
  }
}
