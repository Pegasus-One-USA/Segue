import { Component, computed, signal, viewChild } from '@angular/core';
import { PaginationBarComponent } from '../../../components/shared/pagination-bar/pagination-bar.component';
import { ConnectionListColumn } from '../../../connections/connection-row.model';
import { ConnectionListPage } from '../../../connections/connection-list-page';
import { AUDIENCE_LABELS } from '../../../connections/connection-labels';
import { ConnectionListToolbarComponent } from '../../../connections/components/connection-list-toolbar/connection-list-toolbar.component';
import { ConnectionListTableComponent } from '../../../connections/components/connection-list-table/connection-list-table.component';
import { ConnectionKindCardGroup, ConnectionKindPickerComponent } from '../../../connections/components/connection-kind-picker/connection-kind-picker.component';
import { EhrReadConnectionKindComponent } from '../../components/ehr-read-connection-kind/ehr-read-connection-kind.component';
import { DatabaseConnectionKindComponent } from '../../components/database-connection-kind/database-connection-kind.component';
import {
  EMPTY_SOURCE_FILTERS,
  SourceConnectionFiltersComponent,
  SourceConnectionFilterValue,
  sourceFilterKeys,
} from '../../components/source-connection-filters/source-connection-filters.component';

/**
 * Source Connections: one list of everything a workflow can read from — EHR / FHIR read connections and the SQL
 * databases CSV / SQL Table sources read. Each kind is its own component (loading, permissions, row actions, forms);
 * the shared ConnectionListPage merges their rows and searches, filters, sorts and pages them in the browser. Every
 * row is loaded at once because a tenant's connection lists are small.
 */
@Component({
  selector: 'app-source-connection-list',
  standalone: true,
  imports: [
    PaginationBarComponent,
    ConnectionListToolbarComponent,
    ConnectionListTableComponent,
    ConnectionKindPickerComponent,
    SourceConnectionFiltersComponent,
    EhrReadConnectionKindComponent,
    DatabaseConnectionKindComponent,
  ],
  templateUrl: './source-connection-list.component.html',
  styleUrls: ['./source-connection-list.component.scss'],
})
export class SourceConnectionListComponent extends ConnectionListPage<SourceConnectionFilterValue> {
  readonly ehrKind = viewChild(EhrReadConnectionKindComponent);

  readonly filters = signal<SourceConnectionFilterValue>(EMPTY_SOURCE_FILTERS);
  protected readonly emptyFilters = EMPTY_SOURCE_FILTERS;

  protected filterKeys(filters: SourceConnectionFilterValue): string[] {
    return sourceFilterKeys(filters);
  }

  /** The Kind filter only means something when the role can list both kinds. */
  readonly showKindFilter = computed(() => this.hosts().filter(h => h.canList()).length > 1);
  readonly ehrOptions = computed(() => this.ehrKind()?.ehrOptions() ?? []);
  readonly audienceOptions = Object.entries(AUDIENCE_LABELS).map(([value, label]) => ({ value, label }));

  readonly pickerGroups = computed<ConnectionKindCardGroup[]>(() => [
    { heading: 'EHR', cards: this.cardsOf('ehr-read') },
    { heading: 'Database', cards: this.cardsOf('database') },
  ]);

  readonly columns: ConnectionListColumn[] = [
    { id: 'name', header: 'Name', sortKey: 'name' },
    { id: 'type', header: 'Type', sortKey: 'type' },
    { id: 'audience', header: 'Audience', sortKey: 'audience' },
    { id: 'address', header: 'Base URL' },
    { id: 'clientId', header: 'Client ID' },
    { id: 'status', header: 'Status', sortKey: 'status' },
    { id: 'actionBy', header: 'Action by' },
    { id: 'actionOn', header: 'Action on', sortKey: 'actionOn' },
  ];
  readonly statusLabels = { on: 'Active', off: 'Disabled' };
}
