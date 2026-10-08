import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { of } from 'rxjs';
import { FieldMappingListComponent } from './field-mapping-list.component';
import { FieldMappingAnchorService } from './field-mapping-anchor.service';
import { TransformationRulesService } from './transformation-rules.service';
import { MappingRow } from './field-mapping-model';

/** The mapping list's "MPI Rule" tab: pick which mapped fields the MPI compares to find an existing patient. */
describe('FieldMappingListComponent — MPI Rule tab', () => {
  let fixture: ComponentFixture<FieldMappingListComponent>;
  let host: HTMLElement;

  const valueRow = (targetName: string, fhirPath: string, isMpiMatch?: boolean): MappingRow => ({
    resource: 'Patient', sources: [{ fhirPath, label: fhirPath }], mode: 'value', instance: { type: 'first' },
    targetName, tableName: 'dbo.Patient', ...(isMpiMatch ? { isMpiMatch } : {}),
  });
  const rows: MappingRow[] = [
    valueRow('Identifier', 'Patient.identifier.value', true),
    valueRow('FamilyName', 'Patient.name.family'),
    { resource: 'Patient', sources: [], mode: 'childJson', childNodeId: 'Patient.telecom', targetName: 'Telecom', tableName: 'dbo.Patient' },
    // A fixed value — nothing to match a record on, so never offered.
    { resource: 'Patient', sources: [], mode: 'default', defaultToken: '@default', defaultValue: 'EHR', targetName: 'Origin', tableName: 'dbo.Patient' },
    // Another resource's mapping — not what this screen is editing.
    { ...valueRow('Code', 'Observation.code'), resource: 'Observation', tableName: 'dbo.Observation' },
  ];

  const tabButton = () =>
    Array.from(host.querySelectorAll<HTMLButtonElement>('.fm-list-tab')).find(b => b.textContent?.includes('MPI Rule'));
  const checkboxes = () => Array.from(host.querySelectorAll<HTMLInputElement>('.fm-mpi-check'));

  function render(mpiAvailable: boolean): void {
    fixture.componentRef.setInput('rows', rows);
    fixture.componentRef.setInput('resources', ['Patient']);
    fixture.componentRef.setInput('forest', []);
    fixture.componentRef.setInput('tablesForResource', () => ['dbo.Patient']);
    fixture.componentRef.setInput('columnsFor', () => []);
    fixture.componentRef.setInput('targetByResource', { Patient: 'dbo.Patient' });
    fixture.componentRef.setInput('isApproximated', () => false);
    fixture.componentRef.setInput('rulesDestinationType', 'SqlServer');
    fixture.componentRef.setInput('mpiAvailable', mpiAvailable);
    fixture.componentInstance.collapsed.set(false);
    fixture.detectChanges();
  }

  function openTab(): void {
    tabButton()!.click();
    fixture.detectChanges();
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [FieldMappingListComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        FieldMappingAnchorService,
        { provide: TransformationRulesService, useValue: {
          getNodeSchemas: () => of([]), getEffectiveRules: () => of([]), list: () => of([]),
        } },
      ],
    });
    fixture = TestBed.createComponent(FieldMappingListComponent);
    host = fixture.nativeElement as HTMLElement;
  });

  it('is not offered when no MPI step feeds the destination', () => {
    render(false);
    expect(tabButton()).toBeUndefined();
  });

  it('counts the fields already chosen on its tab', () => {
    render(true);
    expect(tabButton()!.querySelector('.fm-list-count')!.textContent!.trim()).toBe('1');
  });

  it('lists only this resource’s mapped fields — never a fixed-value column', () => {
    render(true);
    openTab();

    const sources = Array.from(host.querySelectorAll('tbody tr td:nth-child(2)')).map(td => td.textContent!.trim());
    expect(sources.length).toBe(3);
    expect(sources.join(' ')).toContain('Patient.identifier.value');
    expect(sources.join(' ')).toContain('Patient.name.family');
    expect(sources.join(' ')).not.toContain('Observation');
    expect(checkboxes().map(box => box.checked)).toEqual([true, false, false]);
  });

  it('reports a field ticked or unticked', () => {
    render(true);
    openTab();
    const changes: { targetName: string; isMpiMatch: boolean }[] = [];
    fixture.componentInstance.mpiMatchChanged.subscribe(e => changes.push({ targetName: e.targetName, isMpiMatch: e.isMpiMatch }));

    checkboxes()[1].click();
    checkboxes()[0].click();

    expect(changes).toEqual([
      { targetName: 'FamilyName', isMpiMatch: true },
      { targetName: 'Identifier', isMpiMatch: false },
    ]);
  });

  it('comes first in the tab bar', () => {
    render(true);
    const labels = Array.from(host.querySelectorAll('.fm-list-tab')).map(tab => tab.textContent!.trim().split(/\s+/)[0]);
    expect(labels[0]).toBe('MPI');
    expect(labels[1]).toBe('Mappings');
  });

  describe('match outcome thresholds', () => {
    const percentInputs = () => Array.from(host.querySelectorAll<HTMLInputElement>('.fm-mpi-percent'));
    const bandText = (modifier: string) => host.querySelector(`.fm-mpi-band--${modifier}`)!.textContent!.replace(/\s+/g, ' ').trim();

    it('shows the thresholds it is given, and what falls below them', () => {
      render(true);
      fixture.componentRef.setInput('mpiThresholds', { autoApprove: 90, manualReview: 70 });
      openTab();

      expect(percentInputs().map(input => input.value)).toEqual(['90', '70']);
      expect(bandText('manual')).toContain('and below 90%');
      expect(bandText('none')).toContain('Score below 70%');
    });

    it('passes every edit up as typed, an empty field included', () => {
      render(true);
      openTab();
      const emitted: unknown[] = [];
      fixture.componentInstance.mpiThresholdsChange.subscribe(value => emitted.push(value));

      const auto = percentInputs()[0];
      auto.value = '95';
      auto.dispatchEvent(new Event('input'));
      const manual = percentInputs()[1];
      manual.value = '';
      manual.dispatchEvent(new Event('input'));

      expect(emitted).toEqual([
        { autoApprove: 95, manualReview: 70 },
        { autoApprove: 90, manualReview: null },
      ]);
    });

    it('marks an overlapping pair invalid under the field, rather than in a toast', () => {
      render(true);
      fixture.componentRef.setInput('mpiThresholds', { autoApprove: 70, manualReview: 80 });
      openTab();

      expect(percentInputs()[1].getAttribute('aria-invalid')).toBe('true');
      expect(host.querySelector('#fm-mpi-manual-error')!.textContent).toContain('Manual review must start below auto-approve (70%).');
      expect(host.querySelector('.fm-mpi-scale')).toBeNull();
    });
  });

  it('falls back to Mappings if the MPI stops feeding the destination while its tab is open', () => {
    render(true);
    openTab();
    fixture.componentRef.setInput('mpiAvailable', false);
    fixture.detectChanges();
    expect(fixture.componentInstance.activeTab()).toBe('mappings');
  });
});
