import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { DestinationWizardComponent } from './destination-wizard.component';
import { PipelineStoreV2 } from '../../../services/pipeline-v2.store';
import { CanvasNode, MpiNode, SourceNode } from '../../../models/node-v2.model';
import { MappingRow } from './field-mapping/field-mapping-model';
import { ISourceConnectionService } from '../../../source-connections/services/i-source-connection.service';

/** App-level providers the wizard injects (app.config.ts); nothing under test calls them. */
const appProviders = [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), { provide: ISourceConnectionService, useValue: {} }];

/**
 * The MPI Rule tab's score thresholds are part of the mapping being edited: kept per resource, put back as they
 * were when the mapping canvas is left without saving, and refused by the canvas's Save while they don't make a
 * valid set of bands.
 */
describe('DestinationWizardComponent — MPI score thresholds', () => {
  let wizard: DestinationWizardComponent;
  let store: PipelineStoreV2;

  const source: SourceNode = { id: 'src', x: 0, y: 0, connected: true, fields: { __name: 'Epic' } };
  const mpi: MpiNode = { id: 'mpi', kind: 'mpi', x: 300, y: 0, fields: {} };
  const patientRow: MappingRow = {
    resource: 'Patient', sources: [{ fhirPath: 'Patient.identifier.value', label: 'Identifier' }], mode: 'value',
    instance: { type: 'first' }, targetName: 'Identifier', tableName: 'dbo.Patient', isMpiMatch: true,
  };

  interface Internals {
    validateMappingForSave(resource: string, ruleExpectedTypeByField: Map<string, unknown>): string[];
    openGroupMapping(resource: string): void;
    confirmExitMapping(): void;
    mpiInPipeline(): boolean;
  }
  const internals = () => wizard as unknown as Internals;

  function create(attachNode: CanvasNode): void {
    TestBed.configureTestingModule({
      imports: [DestinationWizardComponent],
      providers: appProviders,
    });
    // Only the class's state handling is under test — an empty template keeps every step's forms out of it.
    TestBed.overrideComponent(DestinationWizardComponent, { set: { imports: [], template: '' } });
    store = TestBed.inject(PipelineStoreV2);
    store.reset();
    store.addNode(source);
    store.addNode(mpi);
    store.addEdge({ id: 'e1', from: 'src', to: 'mpi' });
    const fixture = TestBed.createComponent(DestinationWizardComponent);
    fixture.componentRef.setInput('destType', 'sql');
    fixture.componentRef.setInput('attachNode', attachNode);
    wizard = fixture.componentInstance;
    wizard.mappingRows.set([patientRow]);
  }

  it('knows an MPI feeds the destination it attaches behind — and not one attached straight to a source', () => {
    create(mpi);
    expect(internals().mpiInPipeline()).toBeTrue();
  });

  it('defaults a resource to auto-approve at 90% and manual review from 70%', () => {
    create(mpi);
    expect(wizard.mpiThresholdsFor('Patient')).toEqual({ autoApprove: 90, manualReview: 70 });
  });

  it('keeps thresholds per resource', () => {
    create(mpi);
    wizard.setMpiThresholds('Patient', { autoApprove: 95, manualReview: 80 });
    expect(wizard.mpiThresholdsFor('Patient')).toEqual({ autoApprove: 95, manualReview: 80 });
    expect(wizard.mpiThresholdsFor('Practitioner')).toEqual({ autoApprove: 90, manualReview: 70 });
  });

  it('puts them back as they were when the mapping canvas is left without saving', () => {
    create(mpi);
    wizard.setMpiThresholds('Patient', { autoApprove: 95, manualReview: 80 });
    internals().openGroupMapping('Patient');

    wizard.setMpiThresholds('Patient', { autoApprove: 60, manualReview: 50 });
    internals().confirmExitMapping();

    expect(wizard.mpiThresholdsFor('Patient')).toEqual({ autoApprove: 95, manualReview: 80 });
  });

  it('restores the saved thresholds when an existing destination is reopened', () => {
    create(mpi);
    const saved: CanvasNode = {
      id: 'dest', kind: 'transform', transformId: 'dest-sqlserver', x: 600, y: 0,
      fields: { dest_mpiThresholds: JSON.stringify({ Patient: { autoApprove: 85, manualReview: 60 } }) },
    } as CanvasNode;

    (wizard as unknown as { _populateFromNode(node: CanvasNode): void })._populateFromNode(saved);

    expect(wizard.mpiThresholdsFor('Patient')).toEqual({ autoApprove: 85, manualReview: 60 });
  });

  it('writes them into the destination’s saved configuration', () => {
    create(mpi);
    wizard.setMpiThresholds('Patient', { autoApprove: 92, manualReview: 75 });
    const emitted: Record<string, string>[] = [];
    wizard.saved.subscribe(event => emitted.push((event as { config: Record<string, string> }).config));

    (wizard as unknown as { _save(): void })._save();

    expect(JSON.parse(emitted[0]['dest_mpiThresholds'])).toEqual({ Patient: { autoApprove: 92, manualReview: 75 } });
  });

  it('refuses to save an invalid pair, naming the threshold', () => {
    create(mpi);
    wizard.setMpiThresholds('Patient', { autoApprove: 70, manualReview: 70 });

    const errors = internals().validateMappingForSave('Patient', new Map());

    expect(errors).toContain('MPI manual review threshold: Manual review must start below auto-approve (70%).');
  });

  it('lets a valid pair through', () => {
    create(mpi);
    wizard.setMpiThresholds('Patient', { autoApprove: 90, manualReview: 70 });
    expect(internals().validateMappingForSave('Patient', new Map()).some(e => e.startsWith('MPI'))).toBeFalse();
  });
});

describe('DestinationWizardComponent — no MPI upstream', () => {
  it('does not offer the MPI Rule tab, nor check its thresholds, for a destination no MPI feeds', () => {
    TestBed.configureTestingModule({
      imports: [DestinationWizardComponent],
      providers: appProviders,
    });
    TestBed.overrideComponent(DestinationWizardComponent, { set: { imports: [], template: '' } });
    const store = TestBed.inject(PipelineStoreV2);
    store.reset();
    const source: SourceNode = { id: 'src', x: 0, y: 0, connected: true, fields: { __name: 'Epic' } };
    store.addNode(source);
    const fixture = TestBed.createComponent(DestinationWizardComponent);
    fixture.componentRef.setInput('destType', 'sql');
    fixture.componentRef.setInput('attachNode', source);
    const wizard = fixture.componentInstance;
    const internals = wizard as unknown as {
      mpiInPipeline(): boolean;
      validateMappingForSave(resource: string, map: Map<string, unknown>): string[];
    };
    wizard.mappingRows.set([{
      resource: 'Patient', sources: [{ fhirPath: 'Patient.id', label: 'Id' }], mode: 'value', instance: { type: 'first' },
      targetName: 'Id', tableName: 'dbo.Patient',
    }]);
    wizard.setMpiThresholds('Patient', { autoApprove: 10, manualReview: 50 });

    expect(internals.mpiInPipeline()).toBeFalse();
    expect(internals.validateMappingForSave('Patient', new Map()).some(e => e.startsWith('MPI'))).toBeFalse();
  });
});
