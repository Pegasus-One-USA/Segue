import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { GovernanceApiService } from '../../services/governance-api.service';

function toDateInputValue(date: Date): string {
  return date.toISOString().slice(0, 10);
}

@Component({
  selector: 'app-compliance-reports',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './compliance-reports.component.html',
  styleUrl: './compliance-reports.component.scss',
})
export class ComplianceReportsComponent {
  private readonly api = inject(GovernanceApiService);

  private readonly today = new Date();
  private readonly thirtyDaysAgo = new Date(this.today.getTime() - 30 * 24 * 60 * 60 * 1000);

  readonly fromDate = signal(toDateInputValue(this.thirtyDaysAgo));
  readonly toDate = signal(toDateInputValue(this.today));
  readonly generating = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly soc2FromDate = signal(toDateInputValue(this.thirtyDaysAgo));
  readonly soc2ToDate = signal(toDateInputValue(this.today));
  readonly soc2Generating = signal(false);
  readonly soc2ErrorMessage = signal<string | null>(null);

  onFromDateChange(value: string): void {
    this.fromDate.set(value);
  }

  onToDateChange(value: string): void {
    this.toDate.set(value);
  }

  onSoc2FromDateChange(value: string): void {
    this.soc2FromDate.set(value);
  }

  onSoc2ToDateChange(value: string): void {
    this.soc2ToDate.set(value);
  }

  private download(blob: Blob, filename: string): void {
    const url = window.URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = filename;
    link.click();
    window.URL.revokeObjectURL(url);
  }

  generate(): void {
    this.generating.set(true);
    this.errorMessage.set(null);

    this.api.hipaaAuditReport(this.fromDate(), this.toDate()).subscribe({
      next: blob => {
        this.generating.set(false);
        this.download(blob, `Segue-Compliance-Report-${this.fromDate()}-${this.toDate()}.pdf`);
      },
      error: () => {
        this.generating.set(false);
        this.errorMessage.set('Failed to generate the report. Check that the date range is valid and try again.');
      },
    });
  }

  generateSoc2(): void {
    this.soc2Generating.set(true);
    this.soc2ErrorMessage.set(null);

    this.api.soc2EvidenceReport(this.soc2FromDate(), this.soc2ToDate()).subscribe({
      next: blob => {
        this.soc2Generating.set(false);
        this.download(blob, `Segue-SOC2-Evidence-${this.soc2FromDate()}-${this.soc2ToDate()}.pdf`);
      },
      error: () => {
        this.soc2Generating.set(false);
        this.soc2ErrorMessage.set('Failed to generate the report. Check that the date range is valid and try again.');
      },
    });
  }
}
