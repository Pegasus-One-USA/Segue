import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { EhrWriteBackDestinationFormComponent } from './ehr-write-back-destination-form.component';
import { ISourceConnectionService } from '../../../../source-connections/services/i-source-connection.service';
import { SourceConnectionModel } from '../../../../source-connections/models/source-connection.model';
import { EhrWriteCapabilities, EhrWriteCapabilitiesService, EhrWriteCapability } from '../../../../services/ehr-write-capabilities.service';

function capability(resourceType: string, extra: Partial<EhrWriteCapability> = {}): EhrWriteCapability {
  return {
    resourceType, operations: ['Create'], vendorApiId: 'x', variant: null, requiresEncounter: false, optInOnly: false,
    allowedApplicationTypes: ['Backend'], liveWriteSupported: true, ...extra,
  };
}

const BY_VENDOR: Record<string, EhrWriteCapabilities> = {
  GenericFhir: {
    vendor: 'GenericFhir', supportsPatientMatch: true, cloneModeEnabled: false,
    capabilities: [capability('AllergyIntolerance'), capability('Goal')],
    testableVendors: ['Epic', 'Healow', 'Athenahealth'],
  },
  Epic: {
    vendor: 'Epic', supportsPatientMatch: true, cloneModeEnabled: false,
    capabilities: [
      capability('AllergyIntolerance'),
      capability('Observation', { variant: 'vital-signs' }),
      capability('Observation', { variant: 'lines-drains-airways', requiresVariantOptIn: true }),
      capability('QuestionnaireResponse', { variant: 'patient-entered-questionnaire', requiresVariantOptIn: true, requiresTargetReferences: true }),
      capability('Procedure', { variant: 'external-radiotherapy-summary', requiresVariantOptIn: true }),
      capability('ServiceRequest', { variant: 'external-radiotherapy-summary', requiresVariantOptIn: true }),
    ],
  },
  Healow: {
    vendor: 'Healow', supportsPatientMatch: false, cloneModeEnabled: false,
    capabilities: [capability('AllergyIntolerance', { requiresVendorActivation: true }), capability('Condition', { createsHolderEncounter: true })],
  },
  Athenahealth: {
    vendor: 'Athenahealth', supportsPatientMatch: true, cloneModeEnabled: false,
    capabilities: [capability('AllergyIntolerance', { requiresVendorActivation: true })],
  },
};

const HAPI = { id: 'hapi', name: 'Local HAPI', sourceSystemType: 'GenericFhir', isEnabled: true, access: 'Write' } as SourceConnectionModel;

/**
 * The EHR Write-Back form on a Generic FHIR connection: "Test as" swaps in the tested vendor's capabilities and
 * options, and the APIs a vendor needs enabled are offered per variant and saved only for the vendor written as.
 */
describe('EhrWriteBackDestinationFormComponent', () => {
  async function createComponent(connections: SourceConnectionModel[] = [HAPI], pick = 'hapi') {
    await TestBed.configureTestingModule({
      imports: [EhrWriteBackDestinationFormComponent],
      providers: [
        { provide: ISourceConnectionService, useValue: { getAll: () => of(connections) } },
        { provide: EhrWriteCapabilitiesService, useValue: { forVendor: (vendor: string) => of(BY_VENDOR[vendor]) } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(EhrWriteBackDestinationFormComponent);
    fixture.detectChanges();
    const form = fixture.componentInstance;
    form.form.controls.sourceConnectionId.setValue(pick);
    form.onTargetChanged();
    fixture.detectChanges();
    return { fixture, form };
  }

  it('writes plain FHIR to a Generic FHIR server until a vendor is chosen to test as', async () => {
    const { form } = await createComponent();

    expect(form.targetVendor()).toBe('GenericFhir');
    expect(form.writableResourceTypes()).toEqual(['AllergyIntolerance', 'Goal']);
    expect(form.getFullConfig()['dest_testAsVendor']).toBe('');
  });

  it('takes the tested vendor\'s types and opt-in APIs, one entry per variant', async () => {
    const { fixture, form } = await createComponent();

    form.form.controls.testAsVendor.setValue('Epic');
    form.onTestAsChanged();
    fixture.detectChanges();

    expect(form.targetVendor()).toBe('Epic');
    expect(form.writableResourceTypes()).toEqual(['AllergyIntolerance', 'Observation', 'QuestionnaireResponse', 'Procedure', 'ServiceRequest']);
    expect(form.effective()!.optInApis.map(api => api.variant))
      .toEqual(['lines-drains-airways', 'patient-entered-questionnaire', 'external-radiotherapy-summary']);
    expect(form.effective()!.optInApis.find(api => api.variant === 'patient-entered-questionnaire')!.tabularOnly).toBeTrue();
    expect(form.getFullConfig()['dest_testAsVendor']).toBe('Epic');
    expect(form.getFullConfig()['dest_ehrVendor']).toBe('Epic');
  });

  it('saves only the enabled APIs the vendor written as offers', async () => {
    const { fixture, form } = await createComponent();
    form.form.controls.testAsVendor.setValue('Epic');
    form.onTestAsChanged();
    fixture.detectChanges();

    form.toggleVariant('lines-drains-airways');
    form.toggleVariant('external-radiotherapy-summary');
    form.toggleVariant('lines-drains-airways');
    expect(form.getFullConfig()['dest_enabledVariants']).toBe('external-radiotherapy-summary');

    form.form.controls.testAsVendor.setValue('Healow');
    form.onTestAsChanged();
    fixture.detectChanges();
    expect(form.getFullConfig()['dest_enabledVariants']).toBe('');
    expect(form.effective()!.offersHolderEncounter).toBeTrue();
    expect(form.effective()!.liveTypes).toContain('AllergyIntolerance', 'a test run never waits for the contract switch');
  });

  it('points to Destination Connections when no connection can take writes', async () => {
    const { fixture } = await createComponent([]);

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Add an EHR write connection under Destination');
    expect(text).not.toContain("Set a connection's Access");
  });

  it("shows an athena connection's department as the default the node field overrides", async () => {
    const athena = {
      id: 'ath', name: 'athena write', sourceSystemType: 'Athenahealth', isEnabled: true, access: 'Write', departmentId: '150',
    } as SourceConnectionModel;
    const { fixture, form } = await createComponent([athena], 'ath');

    const input = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#dw-ewb-department')!;
    expect(input.placeholder).toBe('Connection default: 150');
    expect(form.getFullConfig()['dest_targetDepartmentId']).toBe('', 'an empty node field leaves the connection default in charge');
  });

  it("restores a saved node department over the athena connection's own", async () => {
    const athena = {
      id: 'ath', name: 'athena write', sourceSystemType: 'Athenahealth', isEnabled: true, access: 'Write', departmentId: '150',
    } as SourceConnectionModel;
    const { fixture, form } = await createComponent([athena], 'ath');

    form.patchFrom({ dest_sourceConnectionId: 'ath', dest_targetDepartmentId: '7' });
    fixture.detectChanges();

    const input = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#dw-ewb-department')!;
    expect(input.value).toBe('7');
    expect(form.getFullConfig()['dest_targetDepartmentId']).toBe('7');
  });

  it('restores a saved test run', async () => {
    const { fixture, form } = await createComponent();

    form.patchFrom({ dest_sourceConnectionId: 'hapi', dest_testAsVendor: 'Epic', dest_enabledVariants: 'lines-drains-airways' });
    fixture.detectChanges();

    expect(form.targetVendor()).toBe('Epic');
    expect(form.isVariantEnabled('lines-drains-airways')).toBeTrue();
    expect(form.getFullConfig()['dest_enabledVariants']).toBe('lines-drains-airways');
  });
});
