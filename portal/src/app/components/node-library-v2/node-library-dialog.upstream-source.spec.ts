import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { NodeLibraryDialogComponent } from './node-library-dialog.component';
import { PipelineStoreV2 } from '../../services/pipeline-v2.store';
import { CanvasNode } from '../../models/node-v2.model';
import { ISourceConnectionService } from '../../source-connections/services/i-source-connection.service';
import { SOURCE_TYPES_DECLARED_KEY } from '../../services/upstream-source-v2.util';

/**
 * The destination wizard is fed the types of the source that feeds the destination being configured — walked back
 * through the canvas edges — never the union of every source in the workflow.
 */
describe('NodeLibraryDialogComponent — upstream source of the destination being configured', () => {
  let dialog: NodeLibraryDialogComponent;
  let store: PipelineStoreV2;

  const source = (id: string, fields: Record<string, string>): CanvasNode =>
    ({ id, x: 0, y: 0, connected: true, fields }) as CanvasNode;
  const transform = (id: string, transformId: string): CanvasNode =>
    ({ id, x: 0, y: 0, kind: 'transform', transformId, fields: {} }) as CanvasNode;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [NodeLibraryDialogComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ISourceConnectionService, useValue: { getAll: () => of([]), getById: () => of(null) } },
      ],
    });
    store = TestBed.inject(PipelineStoreV2);
    store.nodes.set([
      source('epic', {
        __name: 'Epic',
        Connector: 'Epic',
        Resources: 'Patient, Condition',
        [SOURCE_TYPES_DECLARED_KEY]: 'true',
        sourceConnectionId: 'c-epic',
      }),
      source('hapi', { __name: 'HAPI', Connector: 'Generic FHIR R4', Resources: 'Patient, Observation' }),
      transform('map-hapi', 'field-mapping'),
      source('rows', { __name: 'Rows', Connector: 'CSV / SQL Table', tab_kind: 'sql', tab_streams: JSON.stringify([{ resourceType: 'Procedure' }]) }),
      transform('map-epic', 'field-mapping'),
      transform('map-rows', 'field-mapping'),
      transform('dest-rows', 'dest-ehr-writeback'),
    ]);
    store.edges.set([
      { id: 'e1', from: 'epic', to: 'map-epic' },
      { id: 'e2', from: 'rows', to: 'map-rows' },
      { id: 'e3', from: 'map-rows', to: 'dest-rows' },
      { id: 'e4', from: 'hapi', to: 'map-hapi' },
    ]);
    dialog = TestBed.createComponent(NodeLibraryDialogComponent).componentInstance;
  });

  it('adding a destination after the Epic branch offers only what Epic reads', () => {
    dialog.destWizardAttach.set(store.byId('map-epic')!);
    expect(dialog.sourceResources()).toEqual(['Patient', 'Condition']);
    expect(dialog.sourceVendor()).toBe('Epic');
    expect(dialog.sourceConnectionId()).toBe('c-epic');
    expect(dialog.sourceIsTabular()).toBeFalse();
  });

  it('editing the destination on the CSV / SQL branch offers only that source\'s types', () => {
    dialog.destWizardAttach.set(store.byId('map-rows')!);
    dialog.destEditNode.set(store.byId('dest-rows')!);
    expect(dialog.sourceResources()).toEqual(['Procedure']);
    expect(dialog.sourceIsTabular()).toBeTrue();
  });

  it('names no vendor for a CSV / SQL Table source, so nothing narrows by another vendor', () => {
    dialog.destWizardAttach.set(store.byId('map-rows')!);
    expect(dialog.sourceVendor()).toBe('');
  });

  it('a Generic FHIR source is GenericFhir; its legacy silent list (no marker) narrows nothing', () => {
    dialog.destWizardAttach.set(store.byId('map-hapi')!);
    expect(dialog.sourceVendor()).toBe('GenericFhir');
    expect(dialog.sourceResources()).toEqual([]);
  });
});
