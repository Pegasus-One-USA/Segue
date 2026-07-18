import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { GovernanceApiService } from '../../services/governance-api.service';
import { AuditLogEntry } from '../../models/governance.model';

interface VersionedEntry {
  entry: AuditLogEntry;
  version: number;
}

interface FieldDiff {
  field: string;
  leftValue: string;
  rightValue: string;
  changed: boolean;
}

function formatValue(value: unknown): string {
  return value === undefined || value === null ? '—' : String(value);
}

function tryParse(json: string | null): Record<string, unknown> {
  if (!json) return {};
  try {
    return JSON.parse(json);
  } catch {
    return {};
  }
}

@Component({
  selector: 'app-configuration-comparison',
  standalone: true,
  imports: [CommonModule, DatePipe],
  templateUrl: './configuration-comparison.component.html',
  styleUrl: './configuration-comparison.component.scss',
})
export class ConfigurationComparisonComponent implements OnInit {
  private readonly api = inject(GovernanceApiService);
  private readonly route = inject(ActivatedRoute);

  readonly loading = signal(false);
  readonly entityType = signal('');
  readonly entityId = signal('');
  readonly versions = signal<VersionedEntry[]>([]);

  readonly leftVersion = signal<number | null>(null);
  readonly rightVersion = signal<number | null>(null);

  readonly leftEntry = computed(() => this.versions().find(v => v.version === this.leftVersion())?.entry ?? null);
  readonly rightEntry = computed(() => this.versions().find(v => v.version === this.rightVersion())?.entry ?? null);

  readonly diffs = computed<FieldDiff[]>(() => {
    const left = this.leftEntry();
    const right = this.rightEntry();
    if (!left || !right) return [];

    const leftObj = tryParse(left.newValueJson);
    const rightObj = tryParse(right.newValueJson);
    const keys = new Set([...Object.keys(leftObj), ...Object.keys(rightObj)]);

    return [...keys]
      .map(field => {
        const leftValue = formatValue(leftObj[field]);
        const rightValue = formatValue(rightObj[field]);
        return { field, leftValue, rightValue, changed: leftValue !== rightValue };
      })
      .sort((a, b) => (a.changed === b.changed ? a.field.localeCompare(b.field) : a.changed ? -1 : 1));
  });

  ngOnInit(): void {
    const params = this.route.snapshot.queryParamMap;
    this.entityType.set(params.get('entityType') ?? '');
    this.entityId.set(params.get('entityId') ?? '');

    if (!this.entityType() || !this.entityId()) {
      return;
    }

    this.loading.set(true);
    this.api.auditLogs(undefined, 200, this.entityType(), this.entityId()).subscribe({
      next: entries => {
        // entries are newest-first (SequenceNumber descending) — oldest is v1.
        const oldestFirst = [...entries].reverse();
        const versioned = oldestFirst.map((entry, index) => ({ entry, version: index + 1 }));
        this.versions.set(versioned);

        if (versioned.length > 0) {
          this.rightVersion.set(versioned[versioned.length - 1].version);
          this.leftVersion.set(versioned.length > 1 ? versioned[versioned.length - 2].version : versioned[0].version);
        }
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  onLeftChange(value: string): void {
    this.leftVersion.set(Number(value));
  }

  onRightChange(value: string): void {
    this.rightVersion.set(Number(value));
  }
}
