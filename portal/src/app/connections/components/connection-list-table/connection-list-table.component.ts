import { Component, computed, input, output } from '@angular/core';
import { DatePipe } from '@angular/common';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatMenuModule } from '@angular/material/menu';
import {
  ConnectionActionId,
  ConnectionListColumn,
  ConnectionRow,
  ConnectionRowAction,
  ConnectionSort,
  ConnectionSortKey,
} from '../../connection-row.model';

/**
 * The table both connection pages show: one row per connection of any kind, the columns the page asks for, and a row
 * menu holding whatever actions the row's own kind offers (no menu when it offers none). Sorting and paging belong
 * to the page; this only reports clicks.
 */
@Component({
  selector: 'app-connection-list-table',
  standalone: true,
  imports: [DatePipe, MatTableModule, MatButtonModule, MatIconModule, MatTooltipModule, MatMenuModule],
  templateUrl: './connection-list-table.component.html',
  styleUrl: './connection-list-table.component.scss',
})
export class ConnectionListTableComponent {
  readonly rows = input.required<ConnectionRow[]>();
  readonly columns = input.required<ConnectionListColumn[]>();
  readonly sort = input.required<ConnectionSort>();
  /** 'src' or 'dest' — prefixes the e2e test ids. */
  readonly testIdPrefix = input.required<string>();
  readonly actionsFor = input.required<(row: ConnectionRow) => ConnectionRowAction[]>();
  readonly statusLabels = input<{ on: string; off: string }>({ on: 'Enabled', off: 'Disabled' });
  readonly emptyTitle = input('');
  readonly emptyHint = input('');
  /** Rows across every page; 0 shows the empty state. */
  readonly total = input(0);

  readonly sortChange = output<ConnectionSortKey>();
  readonly action = output<{ row: ConnectionRow; id: ConnectionActionId }>();

  readonly displayedColumns = computed(() => ['actions', ...this.columns().map(c => c.id)]);

  onHeaderClick(column: ConnectionListColumn): void {
    if (column.sortKey) this.sortChange.emit(column.sortKey);
  }

  /** The text a plain cell shows; "—" when empty. */
  text(row: ConnectionRow, column: ConnectionListColumn): string {
    const values: Partial<Record<ConnectionListColumn['id'], string | null>> = {
      name: row.name,
      type: row.typeLabel,
      audience: row.audience,
      address: row.address,
      clientId: row.clientId,
      writeApis: row.writeApis,
      actionBy: row.actionBy,
    };
    return values[column.id] || '—';
  }
}
