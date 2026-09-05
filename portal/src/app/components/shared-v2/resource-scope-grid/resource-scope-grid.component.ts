import { Component, input, output, computed } from '@angular/core';
import { FHIR_RESOURCES } from '../../../data/scope-constants-v2.data';

@Component({
  selector: 'app-resource-scope-grid',
  standalone: true,
  imports: [],
  templateUrl: './resource-scope-grid.component.html',
  styleUrl: './resource-scope-grid.component.scss',
})
export class ResourceScopeGridComponent {
  readonly selected = input<string[]>([]);
  readonly toggled  = output<{ resource: string; checked: boolean }>();

  readonly resources = FHIR_RESOURCES;

  isSelected(r: string): boolean {
    return this.selected().includes(r);
  }

  toggle(resource: string, event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.toggled.emit({ resource, checked });
  }
}
