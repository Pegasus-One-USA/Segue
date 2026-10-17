import { TestBed } from '@angular/core/testing';
import { TransformNodeComponent } from './transform-node.component';
import { CanvasNode } from '../../../models/node-v2.model';

/**
 * How a saved node looks on the canvas. An EHR Write-Back node always keeps transformId 'dest-ehr-writeback'; it takes
 * its EHR tile's name and badge from its own fields (the vendor a test run stands in for, else the vendor it writes to).
 */
describe('TransformNodeComponent', () => {
  function render(transformId: string, fields: Record<string, string>) {
    TestBed.configureTestingModule({ imports: [TransformNodeComponent] });
    const fixture = TestBed.createComponent(TransformNodeComponent);
    fixture.componentRef.setInput('node', { id: 'n1', x: 0, y: 0, kind: 'transform', transformId, fields } as unknown as CanvasNode);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return {
      name: el.querySelector('.sn-name')?.textContent?.trim(),
      badge: el.querySelector('.sn-abbr')?.textContent?.trim(),
    };
  }

  it('a write-back names its EHR and wears its badge', () => {
    expect(render('dest-ehr-writeback', { dest_ehrVendor: 'Healow' })).toEqual({ name: 'Write to eClinicalWorks', badge: 'ECW' });
  });

  it('a legacy test run saved as GenericFhir shows the EHR it stood in for', () => {
    expect(render('dest-ehr-writeback', { dest_ehrVendor: 'GenericFhir', dest_testAsVendor: 'Epic' }))
      .toEqual({ name: 'Write to Epic', badge: 'EP' });
  });

  it('a test-as vendor that cannot be tested is ignored', () => {
    expect(render('dest-ehr-writeback', { dest_ehrVendor: 'Athenahealth', dest_testAsVendor: 'GenericFhir' }))
      .toEqual({ name: 'Write to athenahealth', badge: 'ATH' });
  });

  it('a write-back naming no EHR keeps the generic look', () => {
    expect(render('dest-ehr-writeback', {})).toEqual({ name: 'EHR Write-Back', badge: 'EWB' });
  });

  it('any other node keeps its own name', () => {
    expect(render('dest-sqlserver', { dest_ehrVendor: 'Epic' }).name).toBe('SQL Server');
  });
});
