import { Component, computed, effect, signal, untracked } from '@angular/core';
import { PaginationBarComponent } from '../../../components/shared/pagination-bar/pagination-bar.component';
import { ConnectionListColumn } from '../../../connections/connection-row.model';
import { ConnectionListPage } from '../../../connections/connection-list-page';
import { ConnectionListToolbarComponent } from '../../../connections/components/connection-list-toolbar/connection-list-toolbar.component';
import { ConnectionListTableComponent } from '../../../connections/components/connection-list-table/connection-list-table.component';
import { ConnectionKindCardGroup, ConnectionKindPickerComponent } from '../../../connections/components/connection-kind-picker/connection-kind-picker.component';
import { DestinationConfigurationKindComponent } from '../../components/destination-configuration-kind/destination-configuration-kind.component';
import { EhrWriteConnectionKindComponent } from '../../components/ehr-write-connection-kind/ehr-write-connection-kind.component';
import {
  DestinationConnectionFiltersComponent,
  DestinationConnectionFilterValue,
  EMPTY_DESTINATION_FILTERS,
} from '../../components/destination-connection-filters/destination-connection-filters.component';
import { DestinationConfigurationDto } from '../../models/destination-configuration.model';

/**
 * Destination Connections: one list of everything a workflow can write to — destinations (DestinationConfiguration
 * rows) and the EHR write connections an EHR Write-Back writes through. Each kind is its own component (loading,
 * permissions, row actions, forms); the shared ConnectionListPage merges their rows and searches, filters, sorts and
 * pages them in the browser. Every row is loaded at once because a tenant's connection lists are small.
 */
@Component({
  selector: 'app-destination-connection-list',
  standalone: true,
  imports: [
    PaginationBarComponent,
    ConnectionListToolbarComponent,
    ConnectionListTableComponent,
    ConnectionKindPickerComponent,
    DestinationConnectionFiltersComponent,
    DestinationConfigurationKindComponent,
    EhrWriteConnectionKindComponent,
  ],
  templateUrl: './destination-connection-list.component.html',
  styleUrls: ['./destination-connection-list.component.scss'],
})
export class DestinationConnectionListComponent extends ConnectionListPage<DestinationConnectionFilterValue> {
  readonly filters = signal<DestinationConnectionFilterValue>(EMPTY_DESTINATION_FILTERS);
  protected readonly emptyFilters = EMPTY_DESTINATION_FILTERS;

  /** One select: a kind, a destination type or a write vendor, each already a row filter key. */
  protected filterKeys(filters: DestinationConnectionFilterValue): string[] {
    return filters.type ? [filters.type] : [];
  }

  readonly canListDestinations = computed(() => !!this.hostOf('destination')?.canList());
  readonly canListWrite = computed(() => !!this.hostOf('ehr-write')?.canList());
  /** Destination types present in the list, so the filter can offer a type the catalog groups leave out. */
  readonly rowTypes = computed(() =>
    (this.rowsByKind().destination ?? []).map(r => (r.raw as DestinationConfigurationDto).destinationType));

  readonly pickerGroups = computed<ConnectionKindCardGroup[]>(() => [
    { heading: 'Destinations', cards: this.cardsOf('destination') },
    { heading: 'EHR', cards: this.cardsOf('ehr-write') },
  ]);

  readonly columns: ConnectionListColumn[] = [
    { id: 'name', header: 'Name', sortKey: 'name' },
    { id: 'type', header: 'Type', sortKey: 'type' },
    { id: 'address', header: 'Address' },
    { id: 'writeApis', header: 'Vendor write APIs' },
    { id: 'status', header: 'Status', sortKey: 'status' },
    { id: 'actionBy', header: 'Action by' },
    { id: 'actionOn', header: 'Action on', sortKey: 'actionOn' },
  ];
  readonly statusLabels = { on: 'Enabled', off: 'Disabled' };

  constructor() {
    super();
    // Destinations gate Edit/Delete on execution history, loaded for the rows actually on screen.
    effect(() => {
      const shown = this.result().items.filter(r => r.kind === 'destination');
      untracked(() => this.hostOf('destination')?.rowsShown?.(shown));
    });
  }
}
