import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { GovernanceApiService } from '../../services/governance-api.service';
import { DataLineage } from '../../models/governance.model';

@Component({
  selector: 'app-data-lineage',
  standalone: true,
  imports: [CommonModule, DatePipe],
  templateUrl: './data-lineage.component.html',
  styleUrl: './data-lineage.component.scss',
})
export class DataLineageComponent implements OnInit {
  private readonly api = inject(GovernanceApiService);
  private readonly route = inject(ActivatedRoute);

  readonly loading = signal(false);
  readonly lineage = signal<DataLineage | null>(null);
  readonly revealedValues = signal<Record<string, string | null>>({});
  readonly revealError = signal<string | null>(null);
  private resourceRecordId = '';

  ngOnInit(): void {
    const id = this.route.snapshot.paramMap.get('resourceRecordId');
    if (!id) return;
    this.resourceRecordId = id;

    this.loading.set(true);
    this.api.dataLineage(id).subscribe({
      next: lineage => { this.lineage.set(lineage); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  reveal(targetField: string): void {
    this.revealError.set(null);
    this.api.revealLineageField(this.resourceRecordId, targetField).subscribe({
      next: result => {
        this.revealedValues.update(map => ({ ...map, [targetField]: result.value }));
      },
      error: () => {
        this.revealError.set(`You don't have permission to reveal field values (requires payload.view).`);
      },
    });
  }

  isRevealed(targetField: string): boolean {
    return targetField in this.revealedValues();
  }

  revealedValue(targetField: string): string {
    const value = this.revealedValues()[targetField];
    return value === null || value === undefined ? '—' : value;
  }
}
