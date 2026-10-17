import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { DestinationWizardComponent } from './destination-wizard.component';
import { CanvasNode } from '../../../models/node-v2.model';
import { EhrWriteCapabilitiesService, EhrWriteCapability } from '../../../services/ehr-write-capabilities.service';
import { ISourceConnectionService } from '../../../source-connections/services/i-source-connection.service';
import { DestinationConfigurationService } from '../../../destination-connections/services/destination-configuration.service';

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

/** A type written only through a kind the destination has to tick (an Epic API variant). */
const variantOnly = (resourceType: string, variant: string): EhrWriteCapability => ({
  ...capability(resourceType),
  variant,
  requiresVariantOptIn: true,
});

const PLAIN_HINT = 'These are the 2 resource types your source reads. Untick any this destination doesn\'t need.';

/**
 * Step 2 ("Resource types") of a NEW destination whose source declares the types it reads starts with all of them
 * ticked (for an EHR, only those it accepts), once. A reopened destination, a chain-node entry and a legacy source
 * keep today's behaviour. The card shows no permission code, and the hint says what the list is in plain words.
 */
describe('DestinationWizardComponent — Step 2 starts ticked', () => {
  let fixture: ComponentFixture<DestinationWizardComponent>;
  let wizard: DestinationWizardComponent;
  let epicCapabilities: EhrWriteCapability[];

  const create = (destType: string, sourceResources: string[], extra: Record<string, unknown> = {}) => {
    fixture = TestBed.createComponent(DestinationWizardComponent);
    wizard = fixture.componentInstance;
    fixture.componentRef.setInput('destType', destType);
    fixture.componentRef.setInput('attachNode', { id: 'src', x: 0, y: 0, connected: true, fields: {} } as CanvasNode);
    fixture.componentRef.setInput('sourceResources', sourceResources);
    for (const [key, value] of Object.entries(extra)) fixture.componentRef.setInput(key, value);
    fixture.detectChanges();
  };
  const goToStep = (n: number) => {
    wizard.step.set(n);
    fixture.detectChanges();
  };
  const loadEhrTarget = () => {
    (wizard as unknown as { _loadEhrWriteTarget(f: Record<string, string>): void })._loadEhrWriteTarget({
      dest_ehrVendor: 'Epic',
      dest_sourceConnectionId: 'conn-1',
    });
    fixture.detectChanges();
  };
  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';

  beforeEach(() => {
    epicCapabilities = [capability('Condition')];
    const destinations = jasmine.createSpyObj<DestinationConfigurationService>('DestinationConfigurationService',
      ['getPaged', 'hasExecutionHistory', 'create', 'update']);
    destinations.getPaged.and.returnValue(of({ items: [], totalCount: 0, page: 1, pageSize: 100 }) as never);
    destinations.hasExecutionHistory.and.returnValue(of({ hasExecutionHistory: false }) as never);

    TestBed.configureTestingModule({
      imports: [DestinationWizardComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: EhrWriteCapabilitiesService,
          useValue: {
            forVendor: () => of({ vendor: 'Epic', supportsPatientMatch: true, cloneModeEnabled: false, capabilities: epicCapabilities }),
            writableResourceTypes: () => of(epicCapabilities.map((c) => c.resourceType)),
          },
        },
        { provide: ISourceConnectionService, useValue: { getAll: () => of([]), getById: () => of({ vendorWriteApisActivated: false }) } },
        { provide: DestinationConfigurationService, useValue: destinations },
      ],
    });
  });

  it('a new destination starts with every type its source reads ticked', () => {
    create('sql', ['Patient', 'Condition']);
    expect(wizard.selectedResources()).toEqual([]);

    goToStep(2);

    expect(wizard.selectedResources()).toEqual(['Patient', 'Condition']);
    expect(wizard.isNextDisabled()).toBeFalse();
  });

  it('a new EHR destination starts with only the types the EHR accepts ticked, never the greyed ones', () => {
    create('ehrwriteback', ['Condition', 'Encounter'], { ehrVendor: 'Epic' });
    goToStep(2);
    // Nothing is ticked until the EHR's accepted types are known.
    expect(wizard.selectedResources()).toEqual([]);

    loadEhrTarget();

    expect(wizard.selectedResources()).toEqual(['Condition']);
  });

  it('a new EHR destination ticks only types with an always-included kind, and turns no optional kind on', () => {
    epicCapabilities = [
      capability('Condition'),
      { ...capability('Observation'), variant: 'vital-signs' },
      variantOnly('Observation', 'lines-drains-airways'),
      variantOnly('BodyStructure', 'radiotherapy-volume'),
    ];
    create('ehrwriteback', ['Condition', 'Observation', 'BodyStructure'], { ehrVendor: 'Epic' });
    goToStep(2);
    loadEhrTarget();

    // BodyStructure is still offered with its kind to tick, just not chosen for the user.
    const bodyStructure = wizard.ehrWriteTypeRows()?.find((r) => r.resourceType === 'BodyStructure');
    expect(bodyStructure?.selectable).toBeTrue();
    expect(bodyStructure?.note).toBeNull();
    expect(wizard.selectedResources()).toEqual(['Condition', 'Observation']);
    // Observation starts with its always-included kind only.
    expect(wizard.ehrReviewTypeLines()).toEqual([
      { resourceType: 'Condition', kinds: [] },
      { resourceType: 'Observation', kinds: ['Vital signs'] },
    ]);
    expect(wizard.ehrSavedKinds()).toEqual({ enabledVariants: [], createHolderEncounter: false });
    expect(wizard.isNextDisabled()).toBeFalse();
  });

  it('a reopened destination keeps exactly its saved selection, even a subset', () => {
    create('sql', ['Patient', 'Condition', 'Encounter'], {
      editNode: { id: 'd1', x: 0, y: 0, connected: true, fields: { dest_resources: 'Condition' } } as CanvasNode,
    });
    goToStep(2);

    expect(wizard.selectedResources()).toEqual(['Condition']);
  });

  it('a legacy source with no declared types starts unticked, as before', () => {
    create('sql', []);
    goToStep(2);

    expect(wizard.selectedResources()).toEqual([]);
  });

  it('a chain-node entry keeps its own selection', () => {
    create('sql', ['Patient', 'Condition'], { chainNodeEntry: true });
    goToStep(2);

    expect(wizard.selectedResources()).toEqual([]);
  });

  it('unticking, then going back and forth, never ticks again', () => {
    create('sql', ['Patient', 'Condition']);
    goToStep(2);
    wizard.toggleResource('Patient');
    fixture.detectChanges();

    goToStep(3);
    goToStep(2);
    expect(wizard.selectedResources()).toEqual(['Condition']);

    wizard.toggleResource('Condition');
    goToStep(1);
    goToStep(2);
    expect(wizard.selectedResources()).toEqual([]);
  });

  it('shows no permission code under the types, and says plainly what the list is', () => {
    create('sql', ['Patient', 'Condition']);
    goToStep(2);

    expect(text()).toContain(PLAIN_HINT);
    expect(text()).not.toContain('user/Patient.read');
    expect(text()).not.toContain('.read');
    expect(text()).not.toContain('filtered to what this source supports');
  });

  it('a source reading one type says so in the singular', () => {
    create('sql', ['Condition']);
    goToStep(2);

    expect(text()).toContain('This is the 1 resource type your source reads.');
  });

  it('the EHR grid uses the same sentence and keeps the greyed-types line', () => {
    create('ehrwriteback', ['Condition', 'Encounter'], { ehrVendor: 'Epic' });
    goToStep(2);
    loadEhrTarget();

    expect(text()).toContain(PLAIN_HINT);
    expect(text()).toContain('Greyed types are read by the source but cannot be written to this EHR.');
    expect(text()).not.toContain('.read');
  });
});
