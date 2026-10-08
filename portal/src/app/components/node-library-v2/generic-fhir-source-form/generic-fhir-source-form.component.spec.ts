import { ComponentFixture, TestBed } from '@angular/core/testing';
import { GenericFhirSourceFormComponent } from './generic-fhir-source-form.component';
import { FHIR_RESOURCES } from '../../../data/scope-constants-v2.data';
import { SOURCE_TYPES_DECLARED_KEY, declaredSourceResourceTypes } from '../../../services/upstream-source-v2.util';

/** A Generic FHIR source declares the resource types it reads — no silent "every type" default any more. */
describe('GenericFhirSourceFormComponent — resource types to read', () => {
  let fixture: ComponentFixture<GenericFhirSourceFormComponent>;
  let form: GenericFhirSourceFormComponent;

  const create = (initialFields: Record<string, string> | null) => {
    fixture = TestBed.createComponent(GenericFhirSourceFormComponent);
    form = fixture.componentInstance;
    fixture.componentRef.setInput('initialFields', initialFields);
    fixture.detectChanges();
  };

  beforeEach(() => TestBed.configureTestingModule({ imports: [GenericFhirSourceFormComponent] }));

  it('starts a new node with nothing selected and refuses to save it', () => {
    create(null);
    form.form.controls.baseUrl.setValue('http://localhost:8080/fhir');
    expect(form.selectedResources()).toEqual([]);
    expect(form.getFields()).toBeNull();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Choose at least one resource type');

    form.onResourceTypesChange(['Patient', 'Condition']);
    const fields = form.getFields();
    expect(fields?.['Resources']).toBe('Patient,Condition');
    expect(fields?.[SOURCE_TYPES_DECLARED_KEY]).toBe('true');
    expect(declaredSourceResourceTypes(fields)).toEqual(['Patient', 'Condition']);
  });

  it('restores a declared list exactly', () => {
    create({
      __name: 'HAPI',
      'FHIR base URL': 'http://localhost:8080/fhir',
      Resources: 'Observation, Patient',
      [SOURCE_TYPES_DECLARED_KEY]: 'true',
    });
    expect(form.selectedResources()).toEqual(['Observation', 'Patient']);
    expect(form.keepsLegacyTypes()).toBeFalse();
    expect(form.getFields()?.['Resources']).toBe('Observation,Patient');
  });

  it('lets a legacy node that never declared any type be re-saved as it is', () => {
    create({ __name: 'HAPI', 'FHIR base URL': 'http://localhost:8080/fhir' });
    expect(form.getFields()).not.toBeNull();
  });

  it('keeps a pre-Step-B node\'s silent 12-type list legacy until the admin changes it', () => {
    const silent = FHIR_RESOURCES.join(',');
    create({ __name: 'HAPI', 'FHIR base URL': 'http://localhost:8080/fhir', Resources: silent, 'Retrieval resource type': silent });
    expect(form.keepsLegacyTypes()).toBeTrue();
    let fields = form.getFields();
    expect(fields?.['Resources']).toBe(silent);
    expect(fields?.[SOURCE_TYPES_DECLARED_KEY]).toBeUndefined();
    expect(declaredSourceResourceTypes(fields)).toBeNull();

    form.onResourceTypesChange([...FHIR_RESOURCES, 'Organization']);
    fields = form.getFields();
    expect(fields?.[SOURCE_TYPES_DECLARED_KEY]).toBe('true');
    expect(declaredSourceResourceTypes(fields)).toContain('Organization');
  });
});
