import { Component, computed, input } from '@angular/core';
import { CdsAction } from '../../models/cds.models';
import { cdsCoverageDecisionLabel } from '../../models/cds-coverage-labels';

/** The Da Vinci CRD coverage-information extension on an updated order. */
export const CDS_COVERAGE_INFORMATION_URL = 'http://hl7.org/fhir/us/davinci-crd/StructureDefinition/ext-coverage-information';

interface OrderUpdate {
  description: string;
  target: string;
  /** Each coverage-information entry on the order, in plain words. */
  decisions: string[];
}

type Json = Record<string, unknown>;

function asObject(value: unknown): Json | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Json : null;
}

function asArray(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}

/** The covered / pa-needed / doc-needed codes of each coverage-information extension on a resource. */
export function cdsCoverageDecisionsOf(resource: Record<string, unknown> | undefined): string[] {
  return asArray(resource?.['extension'])
    .map(asObject)
    .filter((ext): ext is Json => !!ext && ext['url'] === CDS_COVERAGE_INFORMATION_URL)
    .map(ext => {
      const parts = asArray(ext['extension']).map(asObject).filter((p): p is Json => !!p);
      const code = (url: string) => {
        const value = parts.find(p => p['url'] === url)?.['valueCode'];
        return typeof value === 'string' && value ? value : '-';
      };
      return cdsCoverageDecisionLabel(`${code('covered')}/${code('pa-needed')}/${code('doc-needed')}`);
    });
}

/**
 * The changes a coverage requirements service asks the EHR to make by itself (CDS Hooks systemActions): each order it
 * updates and the coverage information written onto it. Shown on the Test screen next to the cards.
 */
@Component({
  selector: 'app-cds-system-actions',
  standalone: true,
  imports: [],
  templateUrl: './cds-system-actions.component.html',
  styleUrl: './cds-system-actions.component.scss',
})
export class CdsSystemActionsComponent {
  readonly actions = input.required<CdsAction[]>();

  readonly updates = computed<OrderUpdate[]>(() => this.actions().map(action => {
    const resource = action.resource;
    const type = typeof resource?.['resourceType'] === 'string' ? resource['resourceType'] as string : null;
    const id = typeof resource?.['id'] === 'string' ? resource['id'] as string : null;
    return {
      description: action.description || (action.type === 'update' ? 'Update an order' : action.type),
      target: type ? (id ? `${type}/${id}` : type) : action.resourceId ?? '',
      decisions: cdsCoverageDecisionsOf(resource),
    };
  }));
}
