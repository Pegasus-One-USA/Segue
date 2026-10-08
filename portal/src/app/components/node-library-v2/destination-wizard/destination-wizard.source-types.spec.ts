import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { DestinationWizardComponent } from './destination-wizard.component';
import { CanvasNode } from '../../../models/node-v2.model';
import { EhrWriteCapabilitiesService, EhrWriteCapability } from '../../../services/ehr-write-capabilities.service';
import { ISourceConnectionService } from '../../../source-connections/services/i-source-connection.service';

const capability = (resourceType: string): EhrWriteCapability => ({
  resourceType,
  operations: ['create'],
  vendorApiId: 'x',
  variant: null,
  requiresEncounter: false,
  optInOnly: false,
  allowedApplicationTypes: ['Backend'],
  liveWriteSupported: true,
});

/**
 * Step 2 ("Data groups") offers only the types the destination's upstream source reads. The wizard is created but
 * never rendered or initialised: these are its pure selection signals, fed by the node library's inputs.
 */
describe('DestinationWizardComponent — narrowed to the upstream source', () => {
  let fixture: ComponentFixture<DestinationWizardComponent>;
  let wizard: DestinationWizardComponent;

  const create = (destType: string, sourceResources: string[]) => {
    fixture = TestBed.createComponent(DestinationWizardComponent);
    wizard = fixture.componentInstance;
    fixture.componentRef.setInput('destType', destType);
    fixture.componentRef.setInput('attachNode', { id: 'src', x: 0, y: 0, connected: true, fields: {} } as CanvasNode);
    fixture.componentRef.setInput('sourceResources', sourceResources);
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [DestinationWizardComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: EhrWriteCapabilitiesService,
          useValue: {
            forVendor: () => of({ vendor: 'Epic', supportsPatientMatch: true, cloneModeEnabled: false, capabilities: [capability('Condition')] }),
            writableResourceTypes: () => of(['Condition']),
          },
        },
        { provide: ISourceConnectionService, useValue: { getById: () => of({ vendorWriteApisActivated: false }) } },
      ],
    });
  });

  it('two sources reading different types: each destination is offered only its own source\'s types', () => {
    create('sql', ['Patient', 'Condition']);
    expect(wizard.availableGroups()).toEqual(['Patient', 'Condition']);

    create('sql', ['Procedure']);
    expect(wizard.availableGroups()).toEqual(['Procedure']);
  });

  it('keeps today\'s full list for a legacy source with no declared types', () => {
    create('sql', []);
    expect(wizard.availableGroups().length).toBeGreaterThan(30);
  });

  it('never recommends a type the source does not read', () => {
    create('sql', ['Patient', 'Condition']);
    wizard.selectedResources.set(['Condition']);
    expect(wizard.recommendedResources()).toEqual([]);

    create('sql', ['Patient', 'Condition', 'Encounter']);
    wizard.selectedResources.set(['Condition']);
    expect(wizard.recommendedResources()).toEqual(['Encounter']);
  });

  it('write-back: offers only the source\'s types the EHR accepts and greys the rest', () => {
    create('ehrwriteback', ['Condition', 'Encounter']);
    (wizard as unknown as { _loadEhrWriteTarget(f: Record<string, string>): void })._loadEhrWriteTarget({
      dest_ehrVendor: 'Epic',
      dest_sourceConnectionId: 'conn-1',
    });

    expect(wizard.availableGroups()).toEqual(['Condition']);
    expect(wizard.ehrWriteTypeRows()).toEqual([
      { resourceType: 'Condition', selectable: true, reason: null, note: null },
      { resourceType: 'Encounter', selectable: false, reason: 'Not accepted by Epic', note: null },
    ]);
    // Condition's 'commonly used together' Encounter is greyed, so it is not recommended either.
    wizard.selectedResources.set(['Condition']);
    expect(wizard.recommendedResources()).toEqual([]);
  });

  it('write-back reopened with a selected type the source no longer reads: shown greyed so it can be unticked', () => {
    create('ehrwriteback', ['Condition']);
    wizard.selectedResources.set(['Condition', 'Immunization']);
    (wizard as unknown as { _loadEhrWriteTarget(f: Record<string, string>): void })._loadEhrWriteTarget({
      dest_ehrVendor: 'Epic',
      dest_sourceConnectionId: 'conn-1',
    });

    expect(wizard.ehrWriteTypeRows()).toEqual([
      { resourceType: 'Condition', selectable: true, reason: null, note: null },
      { resourceType: 'Immunization', selectable: false, reason: 'Not read by this destination\'s source', note: null },
    ]);
    // Offered only what is tickable, so the Step 2 callout still flags it.
    expect(wizard.unsupportedSelectedResources()).toEqual(['Immunization']);
  });
});
