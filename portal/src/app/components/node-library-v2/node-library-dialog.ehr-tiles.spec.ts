import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { DestWizardType, NodeLibraryDialogComponent } from './node-library-dialog.component';
import { PipelineStoreV2 } from '../../services/pipeline-v2.store';
import { CanvasNode } from '../../models/node-v2.model';
import { ISourceConnectionService } from '../../source-connections/services/i-source-connection.service';
import { PermissionService } from '../../auth/services/permission.service';

const EHR_TILES = ['dest-ehr-epic', 'dest-ehr-ecw', 'dest-ehr-athena'];

/**
 * The Node Library offers one destination tile per EHR under an "EHR" heading (the Fabric parent/children pattern).
 * Each tile opens the EHR Write-Back wizard with its vendor preset; every one saves as 'dest-ehr-writeback', and a
 * reopened node finds its tile again from its own fields.
 */
describe('NodeLibraryDialogComponent — EHR destination tiles', () => {
  let dialog: NodeLibraryDialogComponent;
  let store: PipelineStoreV2;
  let granted: Set<string>;

  const source = (id: string): CanvasNode =>
    ({ id, x: 0, y: 0, connected: true, fields: { __name: 'Epic', Connector: 'Epic' } }) as CanvasNode;
  const writeBack = (id: string, fields: Record<string, string>): CanvasNode =>
    ({ id, x: 0, y: 0, kind: 'transform', transformId: 'dest-ehr-writeback', fields }) as CanvasNode;

  function create(rights: string[] = ['ehrwriteback.view', 'ehrwriteback.create', 'ehrwriteback.edit', 'sourceconnections.view',
    'sourceconnections.create', 'sourceconnections.edit']) {
    granted = new Set(rights);
    TestBed.configureTestingModule({
      imports: [NodeLibraryDialogComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ISourceConnectionService, useValue: { getAll: () => of([]), getById: () => of(null) } },
        {
          provide: PermissionService,
          useValue: {
            hasPermission: (code: string) => granted.has(code),
            hasAny: (codes: readonly string[]) => codes.some(c => granted.has(c)),
            hasAll: (codes: readonly string[]) => codes.every(c => granted.has(c)),
            hasNone: (codes: readonly string[]) => !codes.some(c => granted.has(c)),
            matches: () => true,
            hasWorkflowModuleAccess: () => true,
          },
        },
      ],
    });
    store = TestBed.inject(PipelineStoreV2);
    store.nodes.set([source('src')]);
    store.edges.set([]);
    const fixture = TestBed.createComponent(NodeLibraryDialogComponent);
    fixture.componentRef.setInput('mode', 'transform');
    fixture.componentRef.setInput('originNodeId', 'src');
    dialog = fixture.componentInstance;
  }

  const items = () => dialog.filteredCategories().flatMap(c => c.items);
  const ids = () => items().map(i => i.id);
  const item = (id: string) => items().find(i => i.id === id)!;
  const search = (q: string) => dialog.searchQuery.set(q);

  it('offers the EHR heading with one tile per EHR, and never the saved-node id', () => {
    create();
    const all = ids();

    expect(all).toContain('dest-ehr-group');
    for (const tile of EHR_TILES) expect(all).toContain(tile);
    expect(all).not.toContain('dest-ehr-writeback');
    expect(all.indexOf('dest-ehr-group')).toBeLessThan(all.indexOf('dest-ehr-epic'));
    expect(item('dest-ehr-group').isParent).toBeTrue();
    expect(item('dest-ehr-athena').parentId).toBe('dest-ehr-group');
  });

  it('the heading only expands and collapses', () => {
    create();
    dialog.selectItem(item('dest-ehr-group'));

    expect(dialog.showDestWizard()).toBeFalse();
    expect(dialog.isParentExpanded('dest-ehr-group')).toBeTrue();
  });

  it('searching "EHR" lists the heading and every tile under it', () => {
    create();
    search('EHR');

    expect(ids()).toEqual(jasmine.arrayContaining(['dest-ehr-group', ...EHR_TILES]));
  });

  it('searching "athena" expands the heading and shows that tile only', () => {
    create();
    search('athena');

    const ehr = ids().filter(id => id.startsWith('dest-ehr'));
    expect(ehr).toEqual(['dest-ehr-group', 'dest-ehr-athena']);
    expect(dialog.isParentExpanded('dest-ehr-group')).toBeTrue();
  });

  it('hides the tiles, and so the heading, without any EHR Write-Back right', () => {
    create(['sourceconnections.view']);
    expect(ids().filter(id => id.startsWith('dest-ehr'))).toEqual([]);
  });

  it('a tile opens the write-back wizard with its vendor preset', () => {
    create();
    dialog.selectItem(item('dest-ehr-ecw'));

    expect(dialog.showDestWizard()).toBeTrue();
    expect(dialog.destWizardType()).toBe('ehrwriteback');
    expect(dialog.destWizardEhrVendor()).toBe('Healow');
    expect(dialog.destTypeLabel('ehrwriteback', dialog.destWizardEhrVendor())).toBe('Write to eClinicalWorks');
  });

  it('moving to another EHR after progress asks first, and the vendor survives the confirm', () => {
    create();
    dialog.selectItem(item('dest-ehr-epic'));
    dialog.destWizardHasProgressed.set(true);

    dialog.selectItem(item('dest-ehr-athena'));
    expect(dialog.pendingDestSwitch()).toBe('ehrwriteback');
    expect(dialog.pendingDestSwitchEhrVendor()).toBe('Athenahealth');
    expect(dialog.destWizardEhrVendor()).toBe('Epic');

    dialog.confirmDestSwitch();
    expect(dialog.destWizardEhrVendor()).toBe('Athenahealth');
    expect(dialog.pendingDestSwitch()).toBeNull();
    expect(dialog.pendingDestSwitchEhrVendor()).toBeNull();
  });

  it('cancelling the switch clears what was pending, Fabric mode included', () => {
    create();
    dialog.selectItem(item('dest-ehr-epic'));
    dialog.destWizardHasProgressed.set(true);
    dialog.selectItem(item('dest-ehr-athena'));

    dialog.cancelDestSwitch();

    expect(dialog.pendingDestSwitch()).toBeNull();
    expect(dialog.pendingDestSwitchEhrVendor()).toBeNull();
    expect(dialog.pendingDestSwitchFabricMode()).toBeNull();
    expect(dialog.destWizardEhrVendor()).toBe('Epic');
  });

  it('re-clicking the open tile does nothing', () => {
    create();
    dialog.selectItem(item('dest-ehr-epic'));
    dialog.destWizardHasProgressed.set(true);

    dialog.selectItem(item('dest-ehr-epic'));

    expect(dialog.pendingDestSwitch()).toBeNull();
  });

  describe('reopening a saved write-back node', () => {
    const reopen = (fields: Record<string, string>) => {
      const node = writeBack('dest', fields);
      store.nodes.set([source('src'), node]);
      store.edges.set([{ id: 'e1', from: 'src', to: 'dest' }]);
      dialog['_openDestWizardEdit'](node);
    };

    it('finds the tile from the saved vendor', () => {
      create();
      reopen({ dest_ehrVendor: 'Healow' });
      expect(dialog.destWizardType()).toBe('ehrwriteback');
      expect(dialog.destWizardEhrVendor()).toBe('Healow');
    });

    it('a test run reopens on the vendor it stood in for', () => {
      create();
      reopen({ dest_ehrVendor: 'GenericFhir', dest_testAsVendor: 'Epic' });
      expect(dialog.destWizardEhrVendor()).toBe('Epic');
    });

    it('a legacy test run that was also a dry run reopens on the tested EHR', () => {
      create();
      reopen({ dest_ehrVendor: 'GenericFhir', dest_testAsVendor: 'Epic', dest_dryRun: 'true' });
      expect(dialog.destWizardEhrVendor()).toBe('Epic');
    });

    it('ignores a test-as vendor that cannot be tested', () => {
      create();
      reopen({ dest_ehrVendor: 'Athenahealth', dest_testAsVendor: 'GenericFhir' });
      expect(dialog.destWizardEhrVendor()).toBe('Athenahealth');
    });

    it('a node naming no vendor opens without a tile', () => {
      create();
      reopen({ dest_ehrVendor: '' });
      expect(dialog.destWizardEhrVendor()).toBeNull();
    });
  });

  it('locks every destination row while the wizard is past Step 1, the tiles and Lakehouse Delta included', () => {
    create();
    dialog.selectItem(item('dest-ehr-epic'));
    dialog.destWizardStep.set(2);

    const shown = ids();
    for (const tile of EHR_TILES) expect(shown).not.toContain(tile);
    expect(shown).not.toContain('dest-fabric-lakehouse-table');
    expect(shown).not.toContain('dest-sqlserver');
  });

  it('opens the same wizard for every id it opened before', () => {
    const before: Record<string, DestWizardType> = {
      'dest-sqlserver': 'sql', 'dest-csv': 'csv', 'dest-mysql': 'mysql', 'dest-mongo': 'mongo', 'dest-postgres': 'postgres',
      'dest-fhir': 'fhir', 'dest-blob': 'blob', 'dest-medplum': 'medplum', 'dest-azurefhir': 'azurefhir',
      'dest-datalake-webhook': 'datalake', 'dest-fabric': 'fabric', 'dest-fabric-lakehouse-table': 'fabric',
      'dest-fabric-warehouse': 'fabricwarehouse', 'dest-fabric-cosmos': 'cosmosfabric', 'dest-apiendpoint': 'apiendpoint',
      'dest-ehr-writeback': 'ehrwriteback',
    };
    for (const [id, type] of Object.entries(before)) {
      expect(NodeLibraryDialogComponent.destWizardTypeFor(id)).withContext(id).toBe(type);
    }
    for (const tile of EHR_TILES) expect(NodeLibraryDialogComponent.destWizardTypeFor(tile)).toBe('ehrwriteback');
    expect(NodeLibraryDialogComponent.destWizardTypeFor('dest-ehr-group')).toBeNull();
    expect(NodeLibraryDialogComponent.destWizardTypeFor('transformation')).toBeNull();
  });

  describe('Microsoft Fabric still works the same way', () => {
    it('the Lakehouse Delta preset survives the switch confirm', () => {
      create();
      dialog.selectItem(item('dest-fabric'));
      dialog.destWizardHasProgressed.set(true);

      dialog.selectItem(item('dest-fabric-lakehouse-table'));
      dialog.confirmDestSwitch();

      expect(dialog.destWizardType()).toBe('fabric');
      expect(dialog.destWizardFabricMode()).toBe('lakehouseTable');
      expect(dialog.destWizardEhrVendor()).toBeNull();
    });

    it('searching "Fabric" lists its heading and surfaces', () => {
      create();
      search('Fabric');
      expect(ids()).toEqual(jasmine.arrayContaining(
        ['dest-fabric-group', 'dest-fabric', 'dest-fabric-lakehouse-table', 'dest-fabric-warehouse', 'dest-fabric-cosmos']));
    });
  });
});
