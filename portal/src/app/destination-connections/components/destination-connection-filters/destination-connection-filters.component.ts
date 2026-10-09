import { Component, computed, inject, input, output } from '@angular/core';
import { TRANSFORMS } from '../../../data/transforms.data';
import { PhaseConfigService } from '../../../services/phase-config.service';
import { PermissionService } from '../../../auth/services/permission.service';
import { destinationTypeLabel } from '../../../connections/connection-labels';
import { WRITE_VENDORS } from '../../../connections/ehr-write-vendors';

/** `type` is a row filter key itself ('kind:destination', 'destination:SqlServer', 'ehr-write:Epic') or ''. */
export interface DestinationConnectionFilterValue {
  type: string;
  status: '' | 'true' | 'false';
}

export const EMPTY_DESTINATION_FILTERS: DestinationConnectionFilterValue = { type: '', status: '' };

interface TypeGroup {
  label: string;
  options: { value: string; label: string }[];
}

/**
 * The Destination Connections filters. Type: all, all destinations, all EHR write connections, then one option per
 * destination type (grouped by the TRANSFORMS catalog category, gated like the Node Library tiles: phase-enabled and
 * `{prefix}.view`), an "Other types" group for types present in the list but not offered there (e.g. Phase 2+ types),
 * and one option per EHR write vendor. Groups for a kind the role cannot list are left out.
 */
@Component({
  selector: 'app-destination-connection-filters',
  standalone: true,
  templateUrl: './destination-connection-filters.component.html',
  styleUrl: './destination-connection-filters.component.scss',
})
export class DestinationConnectionFiltersComponent {
  private readonly phaseCfg = inject(PhaseConfigService);
  private readonly permissions = inject(PermissionService);

  readonly value = input.required<DestinationConnectionFilterValue>();
  /** The DestinationTypes present in the loaded rows. */
  readonly rowTypes = input<string[]>([]);
  readonly showDestinations = input(true);
  readonly showWrite = input(false);

  readonly valueChange = output<DestinationConnectionFilterValue>();

  /** The catalog groups — built once: the phase config and permissions are static for this screen. */
  private readonly catalogGroups: TypeGroup[] = this.buildCatalogGroups();

  readonly typeGroups = computed<TypeGroup[]>(() => {
    const groups: TypeGroup[] = [];
    if (this.showDestinations()) {
      groups.push(...this.catalogGroups);
      const offered = new Set(this.catalogGroups.flatMap(g => g.options.map(o => o.value)));
      const others = [...new Set(this.rowTypes())]
        .filter(type => !offered.has(`destination:${type}`))
        .map(type => ({ value: `destination:${type}`, label: destinationTypeLabel(type) }));
      if (others.length > 0) groups.push({ label: 'Other types', options: others });
    }
    if (this.showWrite()) {
      groups.push({
        label: 'EHR write connections',
        options: WRITE_VENDORS.map(v => ({ value: `ehr-write:${v.value}`, label: v.label })),
      });
    }
    return groups;
  });

  private buildCatalogGroups(): TypeGroup[] {
    const groups: TypeGroup[] = [];
    for (const t of TRANSFORMS) {
      if (!t.destinationType) continue;
      if (!this.phaseCfg.isTransformEnabled(t.id)) continue;
      if (t.permissionPrefix && !this.permissions.hasPermission(`${t.permissionPrefix}.view`)) continue;
      const label = t.category ?? 'Other';
      let group = groups.find(g => g.label === label);
      if (!group) {
        group = { label, options: [] };
        groups.push(group);
      }
      group.options.push({ value: `destination:${t.destinationType}`, label: t.name });
    }
    return groups;
  }

  onType(type: string): void {
    this.valueChange.emit({ ...this.value(), type });
  }

  onStatus(status: string): void {
    this.valueChange.emit({ ...this.value(), status: status as DestinationConnectionFilterValue['status'] });
  }
}
